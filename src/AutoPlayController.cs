using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Explicitly started, single-match NPC/unranked controller. No match entry,
/// matchmaking, queue, requeue, or automatic restart exists in this class.
/// Every state is observed before an action and re-read immediately before it.
/// Unsupported or ambiguous input pauses instead of guessing/retrying.
/// </summary>
public sealed unsafe class AutoPlayController
{
    private enum Stage { Observing, AwaitChange, AwaitChi, AwaitMeldResolution, AwaitKanResolution }
    private enum Kind { Response, Discard }
    private sealed class PlannedAction
    {
        public Kind Type { get; init; }
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Addon { get; init; } = "";
        public int Option { get; init; } = -1;
        public int Icon { get; init; } = -1;
        public int Slot { get; init; } = -1;
        public string Hand { get; init; } = "";
        public string Evidence { get; init; } = "";
        public int StableMs { get; init; } = 850;
    }
    private readonly record struct Observation(PlannedAction? Action, string Message, bool Halt = false);

    private readonly IGameGui gui;
    private readonly Action<string> report;
    private readonly string diagnosticDirectory;
    private readonly List<string> history = new();
    private Stage stage;
    private PlannedAction? observed;
    private string? activeKey;
    private string? lastIssuedKey;
    private DateTime observedSinceUtc, issuedUtc, nextPollUtc;
    private DateTime chiFailureSinceUtc;
    private string chiFailureReason = "";
    private ChiMeldChoiceTest.ChoiceIdentity? pendingChiIdentity;
    private ChiMeldChoiceTest.Side? pendingChiSide;
    private DateTime chiObservedSinceUtc;
    private int actionCount;
    private string? unreadableObservation;
    private DateTime unreadableSinceUtc;
    private const int UnreadableGraceMs = 2000;
    private const int BlankState15TimeoutMs = 7000;
    private const string BlankState15WaitMessage =
        "State 15: captured blank response panel; waiting without input";
    private DateTime blankState15SinceUtc;
    private const int NumericState15TimeoutMs = 7000;
    private const string NumericState15WaitMessage =
        "State 15: captured numeric triple-tile frame with no choices; waiting without input";
    private DateTime numericState15SinceUtc;
    // The game retains response row labels under a hidden choice list, even
    // with numeric AtkValue[6] (including the observed 22). This is not an
    // actionable menu. Never pause or emit callbacks merely due to an
    // unreadable payload in such a frame. Next remains manual/game-timed.
    private const string HiddenState15WaitMessage =
        "State 15: hidden stale choice list; waiting for round outcome without input";
    // The Riichi/Pass panel may persist even after a declaration/discard.
    // Do not repeat Riichi, send a guessed Pass, or time out on stale labels.
    private const string UnresolvedRiichiState15WaitMessage =
        "State 15: unresolved Riichi/Pass controls after Riichi attempt; waiting without input";
    // A state-15 popup can retain Riichi/Pass labels, even when no Riichi
    // was sent by this controller. State 15 is NOT a self-Riichi action.
    // Do not pause or click based on these residual labels alone.
    private const string ResidualRiichiPassWaitMessage =
        "State 15: Riichi/Pass rows have no named call hint; waiting without input";
    private const string PostRiichiSelfOfferWaitMessage =
        "Self-action Riichi/Pass UI remains after declaration; waiting for resolved discard";
    // Retained across a pause, reset only after a confirmed round boundary.
    private bool riichiIssuedThisRound;
    private const string BetweenRoundsWaitMessage =
        "Round results are displayed; use the game's Next or let its timer advance";
    // State 2 appears between rounds with non-action counter-like text
    // including both captured '× 0' and '× 1'. It is NOT a playable state;
    // do not validate or interpret its text as an action or time it out.
    private const string NextRoundLoadingWaitMessage =
        "Round transition / loading (state 2); waiting without input";
    // State 27 was captured with action 302 during the deal. After an
    // observed results/loading boundary, no state-27 action is authorized:
    // continue observing until the game offers a real playable hint.
    private const string NextRoundDealingWaitMessage =
        "New round is dealing tiles (state 27); waiting without input";
    private const string MissingTableWaitMessage =
        "Mahjong table temporarily not visible; waiting without input";
    private bool betweenRounds;
    private DateTime missingTableSinceUtc;

    public bool Running { get; private set; }
    public bool Paused { get; private set; }
    // Automatic Chi meld selection is deliberately opt-in: second-stage
    // auto selection has not yet been verified by the user in-game.
    public bool AllowChi { get; set; }
    // Kan is unverified in this client. Explicit opt-in; no second-stage guessing.
    public bool AllowKan { get; set; }
    public string Status { get; private set; } = "Stopped (manual start required)";
    public int ActionCount => actionCount;
    public IReadOnlyList<string> History => history;

    public AutoPlayController(IGameGui gameGui, Action<string> log, string snapshotDirectory)
    {
        gui = gameGui;
        report = log;
        diagnosticDirectory = snapshotDirectory;
    }

    private void Log(string message)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
        history.Add(line);
        if (history.Count > 1000) history.RemoveAt(0);
        report("[AUTO] " + message);
    }

    public void SaveHistory(string path) => File.WriteAllLines(path, history);

    public void Start(bool acknowledged)
    {
        if (!acknowledged)
        {
            Status = "Start blocked: acknowledge NPC/unranked play";
            Log(Status);
            return;
        }
        if (Running && !Paused) return;
        if (!IsMahjongVisible())
        {
            Status = "Start blocked: join an NPC/unranked Mahjong table manually first";
            Log(Status);
            return;
        }
        ResetTransient();
        riichiIssuedThisRound = false;
        Running = true;
        Paused = false;
        Status = "Running: observing current table";
        Log("Started in-round only; Next/End Match and queueing remain entirely manual. " +
            (AllowChi ? "Experimental hint-matched Chi enabled. " : "Chi pauses for manual handling. ") +
            (AllowKan ? "Experimental hint-led Kan enabled." : "Kan pauses for manual handling."));
    }

    public void Pause(string reason)
    {
        if (!Running || Paused) return;
        Paused = true;
        // Keep the last issued action across Pause/Resume. If a callback was
        // dispatched but the game UI did not advance, resuming must never
        // replay the same action key merely because the controller reset.
        string? previouslyIssued = lastIssuedKey;
        DateTime previouslyIssuedAt = issuedUtc;
        ResetTransient();
        lastIssuedKey = previouslyIssued;
        issuedUtc = previouslyIssuedAt;
        Status = "PAUSED: " + reason;
        Log(Status);
        // A state-15 popup can disappear before the user has time to run
        // /mahint call. Preserve a read-only snapshot at the exact pause.
        // This never sends a callback, mouse event, or gameplay input.
        if (reason.StartsWith("Opponent call prompt not decoded", StringComparison.Ordinal))
            CaptureUndecodedCall(reason);
        if (reason.StartsWith("Experimental Kan", StringComparison.Ordinal) ||
            reason.StartsWith("Unsupported or unreadable hint", StringComparison.Ordinal))
            CaptureUndecodedCall(reason);
    }

    private void CaptureUndecodedCall(string reason)
    {
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            string path = Path.Combine(diagnosticDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-auto-undecoded-call.txt");
            string diagnostic =
                "# Doman Hint Pilot v0.8.22 — automatic undecoded opponent-call capture" + Environment.NewLine +
                "# Read-only snapshot taken once, at the moment the controller paused." + Environment.NewLine +
                "# This command has not clicked or selected any response." + Environment.NewLine +
                "PauseReason = " + reason + Environment.NewLine +
                RiichiPromptDiagnostics.Inspect(gui);
            File.WriteAllText(path, diagnostic);
            Log("Saved undecoded call UI snapshot: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log("Could not save undecoded call UI snapshot: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Resume(bool acknowledged)
    {
        if (!Running) { Start(acknowledged); return; }
        if (!Paused) return;
        if (!acknowledged)
        {
            Status = "Resume blocked: NPC/unranked acknowledgment is unchecked";
            Log(Status);
            return;
        }
        if (!IsMahjongVisible())
        {
            Status = "Resume blocked: no Mahjong table visible";
            Log(Status);
            return;
        }
        string? previouslyIssued = lastIssuedKey;
        DateTime previouslyIssuedAt = issuedUtc;
        ResetTransient();
        lastIssuedKey = previouslyIssued;
        issuedUtc = previouslyIssuedAt;
        Paused = false;
        Status = "Running: re-observing table";
        Log("Resumed after manual request");
    }

    public void Stop(string reason)
    {
        bool wasRunning = Running;
        Running = false;
        Paused = false;
        ResetTransient();
        riichiIssuedThisRound = false;
        Status = "Stopped: " + reason;
        if (wasRunning) Log(Status);
    }

    private void ResetTransient()
    {
        stage = Stage.Observing;
        observed = null;
        activeKey = lastIssuedKey = null;
        observedSinceUtc = issuedUtc = nextPollUtc = DateTime.MinValue;
        chiFailureSinceUtc = DateTime.MinValue;
        chiFailureReason = "";
        pendingChiIdentity = null;
        pendingChiSide = null;
        chiObservedSinceUtc = DateTime.MinValue;
        unreadableObservation = null;
        unreadableSinceUtc = DateTime.MinValue;
        blankState15SinceUtc = DateTime.MinValue;
        numericState15SinceUtc = DateTime.MinValue;
        betweenRounds = false;
        missingTableSinceUtc = DateTime.MinValue;
    }

    private bool IsMahjongVisible()
    {
        var (name, _) = AddonSnapshot.Peek(gui);
        return name == "Emj" || name == "EmjL";
    }

    private static string Fingerprint(HandScan hand) =>
        string.Join(",", hand.Tiles.Select(t => t.IconId));

    public void Tick(bool acknowledged)
    {
        if (!Running || Paused) return;
        if (!acknowledged) { Pause("NPC/unranked acknowledgment removed"); return; }
        var now = DateTime.UtcNow;
        if (now < nextPollUtc) return;
        nextPollUtc = now.AddMilliseconds(160);
        try { Process(now); }
        catch (Exception ex)
        {
            Pause("Unexpected " + ex.GetType().Name + ": " + ex.Message + " (check Dalamud log)");
        }
    }

    // The final-result addon is inspected read-only. It is never clicked.
    private bool IsFinalResultsVisible()
    {
        var result = gui.GetAddonByName<AtkUnitBase>("EmjTotalResult");
        return result != null && result->IsReady && result->IsVisible;
    }

    private Observation Observe()
    {
        var hint = AddonSnapshot.ReadCurrentHint(gui);
        if (hint.Addon is not ("Emj" or "EmjL"))
            return new(null, MissingTableWaitMessage);

        // Never send a Next callback. Wait for a player click or FFXIV's
        // own result-screen timeout; resume when a new playable hand appears.
        // A state-30 Riichi opportunity is not a round result: only trust
        // its known result label in that state.
        if ((hint.State is 29 or 32) ||
            (hint.State == 30 && HasKnownRoundResultLabel()))
            return new(null, BetweenRoundsWaitMessage);
        // State 2 is the game's non-playable between-round/loading screen.
        // The numeric overlay changes (captured '× 0' and '× 1') while tiles
        // are dealt. No text in this state can authorize a gameplay input.
        if (hint.State == 2)
            return new(null, NextRoundLoadingWaitMessage);
        // State 27 is also non-playable while the round is being dealt, but
        // only classify it as loading after a recognized round boundary.
        if (betweenRounds && hint.State == 27)
            return new(null, NextRoundDealingWaitMessage);

        // A Chi chooser must only be handled as a follow-up to our OWN Chi
        // response. Never act on one encountered unexpectedly.
        if (ChiMeldChoiceTest.TryGetIdentity(gui, out _, out _))
            return new(null, "Chi meld chooser requires a prior controller Chi action", true);

        var prompt = CallPromptReader.Read(gui);
        // The game keeps response label nodes after they cease to be actionable.
        // Never let cached labels under a hidden list authorize a callback.
        if (prompt.IsVerifiedHiddenState15ChoiceList)
            return new(null, HiddenState15WaitMessage);

        // HINT-FIRST: the visible game's recommendation selects the action;
        // response labels merely help resolve an option index or identify an
        // unmistakable conflict. A verified Discard in state 6/30 is allowed
        // to commit directly, even while optional Riichi/Pass is shown.
        // A generic 'Discard' in state 15 is NOT an actionable tile discard.
        var response = HintFirstResponse(hint, prompt);
        if (response.HasValue) return response.Value;

        // State 15 alone does not mean the player has a call to answer.
        // The exact observed zero/empty fields AND absence of clickable choice
        // rows constitute a passive wait. Do not send Pass or a discard.
        if (prompt.IsVerifiedEmptyState15)
            return new(null, "State 15: no active response; waiting without input");

        // 14:57:25 capture: a DIFFERENT blank state-15 layout than the
        // earlier [5]=-1, [6]=0 frame. The outer panel exists, but no
        // active readable choice rows do. Observe it for at most seven
        // seconds. Never infer Pass, Pon or a discard from this condition.
        if (prompt.IsVerifiedBlankState15Panel)
            return new(null, BlankState15WaitMessage);

        // Captured at 15:03:08: action Int(29), three matching tile IDs,
        // but no active choice rows. Do not infer a Kan/Pon response from
        // these numeric values. Wait briefly for a supported next action.
        if (prompt.IsVerifiedNumericTripleState15Frame)
            return new(null, NumericState15WaitMessage);

        if (prompt.State == 15 && prompt.HintVisible)
            return new(null, "Opponent call prompt not decoded: action='" + prompt.RecommendedAction +
                "', options='" + prompt.Summary + "' (auto snapshot on pause)", true);

        // Other numeric states are not interpreted as in-round instructions.
        // State 15 with a real, unrecognized prompt is still handled above.

        if ((hint.State is 6 or 30) && hint.IsDiscardHint)
        {
            var hand = HandTileScanner.Read(gui, hint);
            if (!hand.FullHandRead || hand.Tiles.Count != 14 || hand.MatchingSlots.Count < 1)
                return new(null, "Recommended discard exists but full hand or exact tile match is unreadable", true);
            int slot = hand.MatchingSlots[0];
            if (slot < 0 || slot >= hand.Tiles.Count || hand.Tiles[slot].IconId != hint.IconId)
                return new(null, "Recommended discard slot is inconsistent", true);
            string fp = Fingerprint(hand);
            return new(new PlannedAction {
                Type = Kind.Discard, Key = $"Discard|{hint.Addon}|{hint.State}|{hint.Action}|{hint.IconId}|{fp}",
                Label = "Discard " + hint.TileLabel, Addon = hint.Addon, Icon = hint.IconId,
                Slot = slot, Hand = fp, StableMs = 900 },
                "Recommended discard identified (optional Riichi does not require Pass)");
        }

        // No guess from generic numeric state alone. Unknown action text during
        // a visible panel should stop rather than allow an unintended discard.
        if (hint.PanelVisible && !string.IsNullOrWhiteSpace(hint.Action))
        {
            string a = hint.Action.Trim();
            if (!a.Equals("Discard", StringComparison.OrdinalIgnoreCase))
                return new(null, "Unsupported or unreadable hint '" + a +
                    "' (state " + hint.State + "); handle manually", true);
        }
        return new(null, "Waiting for supported in-game hint");
    }

    private static string NormalizeHint(string? action) =>
        (action ?? "").Trim().TrimEnd('!', '！', '.', '。').Trim();

    private static int UniqueVisibleOption(IReadOnlyList<CallOption> options, string target)
    {
        int found = -1;
        foreach (var option in options)
        {
            if (!string.Equals(option.Label, target, StringComparison.OrdinalIgnoreCase)) continue;
            if (found >= 0) return -1;
            found = option.Option;
        }
        return found;
    }

    // Read-only classification for the post-Riichi state-15 mismatch.
    // Require an exact, currently visible two-row Riichi/Pass list; archived
    // text beneath a hidden choice list is excluded by ReadVisibleChoiceOptions.
    // Numeric values are not named hints. A named win/call hint is never
    // swallowed by this generic-state wait.
    private static bool IsPostRiichiResidualOffer(string action,
        IReadOnlyList<CallOption> visibleOptions)
    {
        if (!int.TryParse(action, out _) &&
            !string.Equals(action, "Discard", StringComparison.OrdinalIgnoreCase))
            return false;
        return visibleOptions.Count == 2 &&
            visibleOptions[0].Option == 0 &&
            visibleOptions[1].Option == 1 &&
            string.Equals(visibleOptions[0].Label, "Riichi", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(visibleOptions[1].Label, "Pass", StringComparison.OrdinalIgnoreCase);
    }

    private Observation? HintFirstResponse(HintReading hint, CallPrompt prompt)
    {
        if (!prompt.HintVisible || prompt.Addon is not ("Emj" or "EmjL")) return null;
        var uiChoices = CallPromptReader.ReadVisibleChoiceOptions(gui, prompt.Addon);
        string action = NormalizeHint(prompt.RecommendedAction);

        // A verified self-draw win outranks every optional self-action.
        if (prompt.HasVerifiedTsumoOffer)
            return ResponseFromHint(prompt, "Tsumo", 0, " [visible Tsumo hint]");

        // State-6 self Kan was previously missed because its raw hint field
        // can be numeric. CallPromptReader now promotes only a verified,
        // exclusive visible "Kan!" hint. The *live* choice list must still
        // identify Kan uniquely. No numeric-value-only callbacks or second
        // meld-selection guesses are allowed.
        if ((prompt.State is 6 or 30) && action == "Kan")
        {
            if (!AllowKan)
                return new Observation(null,
                    "Experimental Kan: explicit self Kan hint seen, but experimental Kan is disabled", true);
            int kanOption = UniqueVisibleOption(uiChoices, "Kan");
            if (kanOption < 0)
                return new Observation(null,
                    "Experimental Kan: visible Kan hint but no unique active Kan response row; handle manually", true);
            return ResponseFromHint(prompt, "Kan", kanOption,
                " [experimental self Kan; visible hint and choice row verified]");
        }

        // RIICHI IS OPTIONAL, NOT AUTOMATIC. An actual Riichi/Pass offer
        // does not mean the game recommends Riichi. Only a named Riichi hint
        // authorizes [11,0]. When the hint instead recommends Discard, do
        // NOT send Pass: discarding the verified tile implicitly declines
        // optional Riichi in the game's UI. Observe() independently verifies
        // the full hand, icon and slot before issuing the normal discard pair.
        // State-15 residual Riichi/Pass labels never take this path.
        bool visibleSelfRiichiPass = (prompt.State is 6 or 30) &&
            uiChoices.Count == 2 &&
            uiChoices[0].Option == 0 && uiChoices[1].Option == 1 &&
            uiChoices[0].Label.Equals("Riichi", StringComparison.OrdinalIgnoreCase) &&
            uiChoices[1].Label.Equals("Pass", StringComparison.OrdinalIgnoreCase);
        bool verifiedSelfRiichiPass = (prompt.State is 6 or 30) &&
            (visibleSelfRiichiPass || prompt.HasRiichiPassOffer);
        if (verifiedSelfRiichiPass)
        {
            // A verified Discard recommendation takes priority over optional
            // Riichi/Pass choices. Do not click Pass first: that callback did
            // not clear the popup in the 2026-11-13 14:52 test. Let Observe()
            // build and confirm the normal two-callback discard directly.
            // A captured post-Riichi discard may have the two-card visual
            // source, so permit it after our own Riichi attempt as well.
            bool discardRecommended = action == "Discard" ||
                (hint.IsDiscardHint && hint.Source == "HintPanel/child007/image") ||
                (riichiIssuedThisRound && hint.IsDiscardHint);
            if (discardRecommended)
            {
                if (hint.IsDiscardHint)
                    return null; // Verify all 14 tiles, matching icon and slot.
                return new Observation(null,
                    "Discard recommended with optional Riichi/Pass, but tile hint is not yet readable; waiting without Pass");
            }

            // Already-declared Riichi is never sent a second time.
            if (riichiIssuedThisRound)
                return new Observation(null, PostRiichiSelfOfferWaitMessage);
            if (action == "Riichi")
                return ResponseFromHint(prompt, "Riichi", 0,
                    " [explicit Riichi recommendation; verified Riichi/Pass offer]");
            return new Observation(null,
                "Riichi/Pass is offered but no explicit Riichi or Discard recommendation was decoded; no input", true);
        }

        if (prompt.State == 15)
        {
            // A winning call takes precedence over generic Discard or stale
            // recommendations ONLY if the genuinely VISIBLE call list
            // explicitly presents Ron (not an old announcement text node).
            int visibleRon = UniqueVisibleOption(uiChoices, "Ron");
            if (visibleRon >= 0 && uiChoices.Any(o => o.Label == "Pass"))
                return ResponseFromHint(prompt, "Ron", visibleRon,
                    " [winning Ron visible; overrides generic hint]");

            // After an attempted Riichi, the game can retain Riichi/Pass
            // under a visible list while AtkValues[6] becomes a numeric
            // payload (observed 0 and 4) or generic Discard. No such value
            // is authority to repeat Riichi or press Pass. This applies to
            // any numeric payload but ONLY with the exact Riichi/Pass pair;
            // explicitly named wins are not swallowed. Genuine Ron was
            // checked above and takes priority.
            if (IsPostRiichiResidualOffer(action, uiChoices))
            {
                if (riichiIssuedThisRound)
                    return new Observation(null, UnresolvedRiichiState15WaitMessage);
                return new Observation(null, ResidualRiichiPassWaitMessage);
            }

            if (prompt.HasUnresolvedState15RiichiPassUi)
            {
                if (riichiIssuedThisRound)
                    return new Observation(null, UnresolvedRiichiState15WaitMessage);
                return new Observation(null, ResidualRiichiPassWaitMessage);
            }

            if (action is "Ron" or "Pon" or "Chi" or "Kan")
            {
                if (action == "Chi" && !AllowChi)
                    return new Observation(null, "Chi hint available; experimental auto-Chi is off", true);
                if (action == "Kan" && !AllowKan)
                    return new Observation(null, "Kan hint available; enable experimental Kan to test", true);
                int matching = UniqueVisibleOption(uiChoices, action);
                // For verified opponent actions, the existing single-action
                // callback uses index 0. If no live labels can be decoded,
                // prefer the stable literal hint over a stale AtkValue layout.
                // Kan is not yet verified and MUST have the live matching row.
                if (action == "Kan" && matching < 0)
                    return new Observation(null,
                        "Kan hint seen but exact Kan option could not be verified; handle manually", true);
                if (matching < 0 && uiChoices.Count > 0)
                    return new Observation(null,
                        "Hint '" + action + "' conflicts with visible response labels; no input", true);
                int option = matching >= 0 ? matching : 0;
                return ResponseFromHint(prompt, action, option,
                    matching >= 0 ? " [hint-selected; visible option index]"
                                 : " [hint-selected; verified standard option 0 fallback]");
            }

            if (action == "Pass")
            {
                int pass = UniqueVisibleOption(uiChoices, "Pass");
                if (pass < 0 && prompt.PassOption >= 0) pass = prompt.PassOption;
                if (pass < 0)
                    return new Observation(null,
                        "Pass hint has no validated Pass index; waiting for readable response", true);
                return ResponseFromHint(prompt, "Pass", pass, " [hint-selected Pass]");
            }

            if (action == "Discard")
            {
                // A generic Discard recommendation on an opponent call
                // corresponds to DECLINING that call. Never discard a tile
                // directly from state 15. Do not interpret Riichi/Pass as
                // an ordinary opponent call after Riichi was attempted.
                int pass = UniqueVisibleOption(uiChoices, "Pass");
                bool ordinaryCall = uiChoices.Any(o =>
                    o.Label is "Pon" or "Chi" or "Kan");
                if (pass >= 0 && ordinaryCall)
                    return ResponseFromHint(prompt, "Pass", pass,
                        " [Discard hint means decline visible opponent call]");
                if (uiChoices.Count > 0)
                    return new Observation(null,
                        "Discard hint with conflicting active choices; awaiting clear hint", true);
            }
        }
        return null;
    }

    private static Observation ResponseFromHint(CallPrompt prompt, string action, int option, string evidence) =>
        new(new PlannedAction {
            Type = Kind.Response, Label = action, Addon = prompt.Addon, Option = option,
            Key = "HintResponse|" + prompt.Signature + "|" + action + "|" + option,
            Evidence = evidence, StableMs = 900
        }, "Validating hint-led " + action + " (option " + option + ")");

    private static Observation Response(CallPrompt prompt, string label, int option) =>
        new(new PlannedAction {
            Type = Kind.Response, Label = label, Addon = prompt.Addon, Option = option,
            Key = "Response|" + prompt.Signature + "|" + label + "|" + option,
            Evidence = prompt.HasDiscardAsPassFallback
                ? " [Pon/Pass UI fallback: Discard hint mapped to Pass]" : "",
            StableMs = 850 },
            prompt.HasDiscardAsPassFallback
                ? "Verifying Pass (visible Pon/Pass popup; Discard hint mapped to Pass)"
                : "Verifying " + label + " response");

    private void Process(DateTime now)
    {
        // The controller never clicks Next/End Match. Final results stop the
        // controller; a new match still requires a fresh manual Start.
        if (IsFinalResultsVisible())
        {
            Stop("Final match results shown; close manually or let the game time out");
            return;
        }

        var boundary = Observe();
        bool roundWaiting = boundary.Message is BetweenRoundsWaitMessage or NextRoundLoadingWaitMessage or NextRoundDealingWaitMessage;
        if (roundWaiting)
        {
            if (!betweenRounds)
                Log("Round transition detected; Next is manual or game-timed. No round-control inputs sent.");
            if (boundary.Message == NextRoundDealingWaitMessage &&
                Status != "Running: " + NextRoundDealingWaitMessage)
                Log("Observed new-round dealing state 27; waiting for a playable hint without input or timeout.");
            betweenRounds = true;
            stage = Stage.Observing;
            observed = null;
            activeKey = null;
            observedSinceUtc = DateTime.MinValue;
            lastIssuedKey = null; // new round will have a different action identity
            Status = "Running: " + boundary.Message;
            return;
        }

        // The Emj addon can temporarily disappear during results/loading.
        // Do not pause on that temporary absence, but do not run forever
        // after the game has completely closed either.
        if (boundary.Message == MissingTableWaitMessage)
        {
            if (missingTableSinceUtc == DateTime.MinValue)
            {
                missingTableSinceUtc = now;
                Log("Mahjong interface not visible; waiting for game transition (no input).");
            }
            if ((now - missingTableSinceUtc).TotalSeconds >= 30)
            {
                Stop("Mahjong interface no longer visible; no automatic match entry or restart");
                return;
            }
            Status = "Running: " + MissingTableWaitMessage;
            return;
        }
        missingTableSinceUtc = DateTime.MinValue;

        if (betweenRounds && boundary.Action is not null)
        {
            betweenRounds = false;
            riichiIssuedThisRound = false;
            lastIssuedKey = null;
            observed = null;
            observedSinceUtc = DateTime.MinValue;
            activeKey = null;
            Log("New playable round detected; in-round automation resumed.");
        }

        if (stage == Stage.AwaitChi) { ProcessChi(now); return; }
        if (stage == Stage.AwaitKanResolution) { ProcessKan(now, boundary); return; }
        if (stage == Stage.AwaitMeldResolution)
        {
            if (ChiMeldChoiceTest.TryGetIdentity(gui, out _, out _))
            {
                if ((now - issuedUtc).TotalSeconds > 8)
                    Pause("Chi chooser remained after a single selection; no retry");
                return;
            }
            if ((now - issuedUtc).TotalMilliseconds < 600) return;
            stage = Stage.Observing;
            observed = null;
            observedSinceUtc = now;
            Status = "Running: Chi chooser closed; observing hand";
            return;
        }
        var found = boundary;
        if (stage == Stage.AwaitChange)
        {
            if (found.Action?.Key == activeKey)
            {
                if ((now - issuedUtc).TotalSeconds > 10)
                    Pause("Game state unchanged after action; no automatic retry");
                return;
            }
            if ((now - issuedUtc).TotalMilliseconds < 550) return;
            // A changed or cleared action is needed before the next action.
            stage = Stage.Observing;
            observed = null;
            observedSinceUtc = now;
            activeKey = null;
            Status = "Running: awaiting stable next hint";
            return;
        }

        // Keep the bounded blank-panel timer only while this exact,
        // read-only signature persists; any other observation resets it.
        if (found.Action is not null || found.Halt ||
            found.Message != BlankState15WaitMessage)
            blankState15SinceUtc = DateTime.MinValue;
        if (found.Action is not null || found.Halt ||
            found.Message != NumericState15WaitMessage)
            numericState15SinceUtc = DateTime.MinValue;

        if (found.Halt)
        {
            // The 14:08:22 capture paused 26ms after Start on a transient
            // unreadable action ('× 0'). Allow a small stable grace window for
            // these specific not-yet-decoded hints, never act on them.
            bool unreadable = found.Message.StartsWith("Unsupported or unreadable hint '", StringComparison.Ordinal) ||
                found.Message.StartsWith("Opponent call prompt not decoded", StringComparison.Ordinal);
            if (unreadable)
            {
                if (unreadableObservation != found.Message)
                {
                    unreadableObservation = found.Message;
                    unreadableSinceUtc = now;
                    Status = "Running: waiting for transient UI hint to settle";
                    return;
                }
                if ((now - unreadableSinceUtc).TotalMilliseconds < UnreadableGraceMs)
                    return;
            }
            Pause(found.Message);
            return;
        }
        unreadableObservation = null;
        unreadableSinceUtc = DateTime.MinValue;
        var candidate = found.Action;
        if (candidate is null)
        {
            if (found.Message == HiddenState15WaitMessage &&
                Status != "Running: " + HiddenState15WaitMessage)
                Log("State-15 choice list is hidden (raw action may be numeric); waiting for next playable hint without input.");
            if (found.Message == BlankState15WaitMessage)
            {
                if (blankState15SinceUtc == DateTime.MinValue)
                {
                    blankState15SinceUtc = now;
                    Log("Observed blank state-15 choice panel; waiting up to seven seconds without input.");
                }
                if ((now - blankState15SinceUtc).TotalMilliseconds >= BlankState15TimeoutMs)
                {
                    Pause("Opponent call prompt not decoded: blank state-15 panel persisted seven seconds; no input sent");
                    return;
                }
            }
            if (found.Message == NumericState15WaitMessage)
            {
                if (numericState15SinceUtc == DateTime.MinValue)
                {
                    numericState15SinceUtc = now;
                    Log("Observed numeric state-15 triple-tile frame without visible responses; waiting up to seven seconds without input.");
                }
                if ((now - numericState15SinceUtc).TotalMilliseconds >= NumericState15TimeoutMs)
                {
                    Pause("Opponent call prompt not decoded: numeric state-15 triple-tile frame persisted seven seconds; no input sent");
                    return;
                }
            }
            if (found.Message == UnresolvedRiichiState15WaitMessage &&
                Status != "Running: " + UnresolvedRiichiState15WaitMessage)
                Log("Post-Riichi state-15 Riichi/Pass rows persist; waiting without repeat input or timeout.");
            if (found.Message == ResidualRiichiPassWaitMessage &&
                Status != "Running: " + ResidualRiichiPassWaitMessage)
                Log("State-15 Riichi/Pass labels remain without an active named call; waiting without input.");
            if (found.Message == PostRiichiSelfOfferWaitMessage &&
                Status != "Running: " + PostRiichiSelfOfferWaitMessage)
                Log("Riichi/Pass self-action panel persists after Riichi; waiting for the proper discard state.");
            if (found.Message == "State 15: no active response; waiting without input" &&
                Status != "Running: " + found.Message)
                Log("Observed empty state-15 response frame; no clickable choices. Waiting without input.");
            observed = null;
            observedSinceUtc = DateTime.MinValue;
            Status = "Running: " + found.Message;
            return;
        }
        if (candidate.Key == lastIssuedKey)
        {
            Status = "Running: waiting for previously handled action to clear";
            if ((now - issuedUtc).TotalSeconds > 10)
                Pause("Previously handled action reappeared unchanged; no duplicate input");
            return;
        }
        if (observed is null || observed.Key != candidate.Key)
        {
            observed = candidate;
            observedSinceUtc = now;
            Status = "Running: validating " + candidate.Label;
            return;
        }
        if ((now - observedSinceUtc).TotalMilliseconds < candidate.StableMs) return;

        // Re-read everything immediately before sending. The UI can change
        // between the planning poll and the moment of dispatch.
        var confirmed = Observe();
        if (confirmed.Halt || confirmed.Action is null ||
            confirmed.Action.Key != candidate.Key || confirmed.Action.Type != candidate.Type)
        {
            observed = null;
            Status = "Running: candidate changed, re-observing";
            return;
        }
        Execute(candidate, now);
    }

    private void Execute(PlannedAction plan, DateTime now)
    {
        // Mark the key consumed BEFORE dispatch; a failure never auto-retries.
        activeKey = plan.Key;
        lastIssuedKey = plan.Key;
        issuedUtc = now;
        switch (plan.Type)
        {
            case Kind.Response:
                if (plan.Option < 0 || plan.Option > 7 ||
                    !TryDispatch(plan.Addon, 11, plan.Option, out bool responseReturned))
                {
                    Pause("Response callback could not be dispatched; no retry");
                    return;
                }
                actionCount++;
                if (plan.Label.Equals("Riichi", StringComparison.OrdinalIgnoreCase))
                    riichiIssuedThisRound = true;
                Log($"Response attempted: {plan.Label} (option {plan.Option}){plan.Evidence}; " +
                    $"FireCallback returned {responseReturned}. Awaiting UI change; no retry.");
                if (plan.Label.Equals("Kan", StringComparison.OrdinalIgnoreCase))
                {
                    // Kan may open a second selection UI we have NOT captured.
                    // Observe passively, never infer a follow-up callback.
                    stage = Stage.AwaitKanResolution;
                    Status = "Running: waiting for experimental Kan resolution";
                }
                else if (plan.Label.Equals("Chi", StringComparison.OrdinalIgnoreCase))
                {
                    stage = Stage.AwaitChi;
                    pendingChiIdentity = null;
                    pendingChiSide = null;
                    chiObservedSinceUtc = DateTime.MinValue;
                    Status = "Running: waiting for Chi meld chooser";
                }
                else
                {
                    stage = Stage.AwaitChange;
                    Status = "Running: waiting for " + plan.Label + " to resolve";
                }
                break;
            case Kind.Discard:
                // Mirror the ALREADY USER-VERIFIED manual v0.7.x sequence:
                // validate the complete hand and hint BEFORE highlighting,
                // then fire [15, icon] and [7, slot] in the SAME update.
                // The game may alter selected/visible tile nodes after [15],
                // so a 220ms delayed full-hand fingerprint comparison can
                // reject a valid selection. Do not re-read a potentially
                // mutated view between these two callbacks.
                if (plan.Icon < 76041 || plan.Icon > 76077 ||
                    plan.Slot < 0 || plan.Slot > 13)
                {
                    Pause("Discard plan invalid; no callbacks sent");
                    return;
                }
                var live = Observe();
                if (live.Halt || live.Action is null ||
                    live.Action.Type != Kind.Discard || live.Action.Key != plan.Key ||
                    live.Action.Icon != plan.Icon || live.Action.Slot != plan.Slot ||
                    live.Action.Hand != plan.Hand)
                {
                    Pause("Discard changed BEFORE highlight; no callbacks sent");
                    return;
                }
                if (plan.Addon is not ("Emj" or "EmjL"))
                {
                    Pause("Unexpected discard addon; no callbacks sent");
                    return;
                }
                var discardAddon = gui.GetAddonByName<AtkUnitBase>(plan.Addon);
                if (discardAddon == null || !discardAddon->IsReady || !discardAddon->IsVisible)
                {
                    Pause("Discard addon no longer ready; no callbacks sent");
                    return;
                }
                var discardArgs = stackalloc AtkValue[2];
                discardArgs[0] = new AtkValue { Type = AtkValueType.Int, Int = 15 };
                discardArgs[1] = new AtkValue { Type = AtkValueType.Int, Int = plan.Icon };
                bool highlightReturned = discardAddon->FireCallback(2, discardArgs);
                discardArgs[0].Int = 7;
                discardArgs[1].Int = plan.Slot;
                bool commitReturned = discardAddon->FireCallback(2, discardArgs);
                actionCount++;
                stage = Stage.AwaitChange;
                Status = "Running: awaiting discard to resolve";
                Log($"Discard pair attempted in one update: {plan.Label} " +
                    $"(icon {plan.Icon}, slot {plan.Slot + 1}); " +
                    $"highlight returned {highlightReturned}, commit returned {commitReturned}. " +
                    "Waiting for UI change; no retry.");
                break;
        }
    }

    private bool HasVisibleUnknownMeldChooser()
    {
        try
        {
            var addon = gui.GetAddonByName<AtkUnitBase>("Emj");
            if (addon == null || !addon->IsVisible || !addon->IsReady ||
                addon->UldManager.NodeList == null ||
                addon->UldManager.NodeListCount <= 280) return false;
            var root = addon->UldManager.NodeList[280];
            return root != null && root->IsVisible() && (int)root->Type >= 1000;
        }
        catch { return false; }
    }

    private void ProcessKan(DateTime now, Observation current)
    {
        // The supplement draw after Kan can produce a winning hand (Tsumo).
        // The 2026-11-13 16:13:54 capture confirms state 6, a visible
        // Tsumo! recommendation and live Tsumo/Pass choices after our Kan.
        // Do not strand a real win behind the experimental seven-second Kan
        // timeout. Re-enter the normal, STABILIZED response validation path;
        // never dispatch a winning callback directly from this state handler.
        if (!current.Halt && current.Action is { Type: Kind.Response } win &&
            (win.Label is "Tsumo" or "Ron") &&
            win.Key != lastIssuedKey &&
            (now - issuedUtc).TotalMilliseconds >= 500)
        {
            stage = Stage.Observing;
            observed = null;
            observedSinceUtc = DateTime.MinValue;
            activeKey = null;
            Status = "Running: validating winning hint after experimental Kan";
            Log("Kan follow-up presented verified " + win.Label +
                "; handing off to ordinary stabilized win handling. No Kan retry.");
            return;
        }

        // No speculative second-stage Kan meld/closed-kan selection callbacks.
        // Wait for a genuine playable hint or completed-round result.
        if (IsFinalResultsVisible() ||
            (AddonSnapshot.ReadCurrentHint(gui).State is 29 or 32) ||
            HasKnownRoundResultLabel())
        {
            stage = Stage.Observing;
            observed = null;
            activeKey = null;
            return;
        }
        if (HasVisibleUnknownMeldChooser())
        {
            Pause("Experimental Kan opened an unverified meld chooser; manual selection required");
            return;
        }
        var hint = AddonSnapshot.ReadCurrentHint(gui);
        if ((hint.State is 6 or 30) && hint.IsDiscardHint &&
            (now - issuedUtc).TotalMilliseconds > 700)
        {
            stage = Stage.Observing;
            observed = null;
            activeKey = null;
            Log("Kan resolved to a normal discard; no second Kan callback sent.");
            return;
        }
        if ((now - issuedUtc).TotalSeconds >= 7)
            Pause("Experimental Kan did not resolve to a known turn; inspect screen, handle manually, do not retry");
    }

    private void ProcessChi(DateTime now)
    {
        if (!AllowChi) { Pause("Auto-Chi was switched off while resolving a meld"); return; }
        if ((now - issuedUtc).TotalSeconds > 7)
        {
            Pause("No confirmed Chi meld choice after 7 seconds; select manually");
            return;
        }
        // Only a *unique exact tile match* authorizes either candidate.
        // This currently supports only the observed 2-choice layout.
        if (ChiMeldChoiceTest.TryGetIdentity(gui, out _, out _))
        {
            if (!ChiMeldChoiceTest.TryGetHintMatch(gui, out var identity,
                    out var side, out var detail))
            {
                // Wait for the chooser's hint images to settle before
                // treating the mismatch as fatal. Never guess an option.
                if (chiFailureReason != detail)
                {
                    chiFailureReason = detail;
                    chiFailureSinceUtc = now;
                }
                if ((now - chiFailureSinceUtc).TotalMilliseconds > 1000)
                    Pause("Chi chooser is ambiguous: " + detail + " Choose manually.");
                return;
            }
            chiFailureReason = "";
            chiFailureSinceUtc = DateTime.MinValue;
            if (pendingChiIdentity is null || pendingChiIdentity.Value != identity ||
                pendingChiSide != side)
            {
                pendingChiIdentity = identity;
                pendingChiSide = side;
                chiObservedSinceUtc = now;
                Status = "Running: validating " + side + " Chi meld against hint";
                return;
            }
            if ((now - chiObservedSinceUtc).TotalMilliseconds < 500) return;
            // Consume first, read back the hint and button registration again.
            pendingChiIdentity = null;
            pendingChiSide = null;
            if (!ChiMeldChoiceTest.TryClickHintMatchOnce(gui, identity, side, out detail))
            {
                Pause("Chi meld click refused: " + detail);
                return;
            }
            actionCount++;
            issuedUtc = now;
            stage = Stage.AwaitMeldResolution;
            Status = "Running: Chi combination selected; awaiting chooser closure";
            Log("Selected unique hint-matching Chi meld " + side + " once: " + detail);
            return;
        }
        // Some single-choice Chi results may resolve without opening the
        // captured two-choice panel. We do NOT guess a meld index in that case.
        if ((now - issuedUtc).TotalMilliseconds > 750)
        {
            var hint = AddonSnapshot.ReadCurrentHint(gui);
            if ((hint.Addon is "Emj" or "EmjL") && hint.IsDiscardHint &&
                (hint.State is 6 or 30))
            {
                stage = Stage.Observing;
                observed = null;
                activeKey = null;
                observedSinceUtc = DateTime.MinValue;
                Status = "Running: Chi resolved without two-choice chooser";
            }
        }
    }

    private bool HasKnownRoundResultLabel()
    {
        var addon = gui.GetAddonByName<AtkUnitBase>("Emj");
        if (addon == null || !addon->IsVisible || addon->AtkValues == null ||
            addon->AtkValuesCount <= 5) return false;
        string label;
        try { label = addon->AtkValues[5].GetValueAsString().Trim(); }
        catch { return true; } // Cannot confirm the results cleared.
        return label is "Draw" or "Called Ron" or "Called Tsumo" or "Ron" or "Tsumo";
    }

    /// <summary>
    /// True means the callback was invoked on an existing, ready, visible addon;
    /// it does NOT mean the game accepted the action. The FireCallback return
    /// value is recorded separately and never treated as proof of acceptance
    /// or rejection. The controller observes UI state changes and never retries
    /// an attempted input automatically.
    /// </summary>
    private bool TryDispatch(string addonName, int opcode, int value, out bool callbackReturned)
    {
        callbackReturned = false;
        if (addonName is not ("Emj" or "EmjL")) return false;
        var addon = gui.GetAddonByName<AtkUnitBase>(addonName);
        if (addon == null || !addon->IsReady || !addon->IsVisible) return false;
        var args = stackalloc AtkValue[2];
        args[0] = new AtkValue { Type = AtkValueType.Int, Int = opcode };
        args[1] = new AtkValue { Type = AtkValueType.Int, Int = value };
        callbackReturned = addon->FireCallback(2, args);
        return true;
    }
}
