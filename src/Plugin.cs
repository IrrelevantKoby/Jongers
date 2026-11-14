using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; set; } = null!;
    [PluginService] internal static IPluginLog Log { get; set; } = null!;

    private bool windowOpen = true;
    private string tableName = "(none)";
    private string lastStatus = "Ready to capture a UI snapshot";
    private int stateCode = -1;
    private HintReading liveHint = HintReading.None;
    private HandScan handScan = HandScan.Empty;
    private CallPrompt callPrompt = CallPrompt.None;
    private bool observeChanges;
    // Single-use opt-in, never persisted. This does not verify the matchmaking mode.
    private bool explicitlyUnrankedTest;
    // Explicit user preference: when the UI offers Riichi / Pass, Riichi
    // takes priority over a simultaneous Discard hint. Off by toggling in UI.
    // Nothing is sent automatically; both prepare and confirm remain required.
    private bool prioritizeRiichi = false;
    private bool autoChiHintMatch = true;
    private bool autoAllowChi;
    private bool autoAllowKan;
    private readonly AutoPlayController controller;
    private bool pendingRiichiPriority;
    private string? pendingHandFingerprint;
    private int pendingIcon = -1;
    private int pendingSlot = -1;
    private DateTime pendingUntilUtc = DateTime.MinValue;
    private string? pendingCallSignature;
    private int pendingResponseOption = -1;
    private string? pendingResponseLabel;
    private DateTime pendingCallUntilUtc = DateTime.MinValue;
    private bool pendingChiChoice;
    private ChiMeldChoiceTest.ChoiceIdentity? pendingChiIdentity;
    private ChiMeldChoiceTest.Side pendingChiSide;
    private DateTime pendingChiUntilUtc = DateTime.MinValue;
    // A one-shot second-stage action, armed ONLY by the user's confirmed Chi.
    // Two stable passive observations are required before any event is sent.
    private bool autoChiArmed;
    private DateTime autoChiUntilUtc = DateTime.MinValue;
    private ChiMeldChoiceTest.ChoiceIdentity? autoChiObservedIdentity;
    private ChiMeldChoiceTest.Side? autoChiObservedSide;
    private DateTime autoChiFirstObservedUtc = DateTime.MinValue;
    private string autoChiLastReason = "Waiting for the Chi chooser.";

    private DateTime nextPollUtc;
    private AddonSnapshot? hintsOff;
    private AddonSnapshot? hintsOn;
    private readonly Queue<string> events = new();
    private readonly string snapshotsDirectory;

    public Plugin()
    {
        snapshotsDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "snapshots");
        Directory.CreateDirectory(snapshotsDirectory);
        controller = new AutoPlayController(GameGui, AddEvent, snapshotsDirectory);
        CommandManager.AddHandler("/mahint", new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Doman Hint Pilot; /mahint auto start|pause|resume|stop controls single-match automation. No queueing. /mahint results, chi, call, hint, riichi, addons save diagnostics."
        });
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        AddEvent("v0.8.19 hint-led in-round controller ready but STOPPED. Results and End Match are handled in FFXIV; no queue/requeue.");
    }

    private void OpenUi() => windowOpen = true;

    private void StartAutomation()
    {
        ClearAutoChi();
        ClearPendingChiChoice();
        ClearPendingDiscard();
        ClearPendingResponse();
        controller.AllowChi = autoAllowChi;
        controller.AllowKan = autoAllowKan;
        controller.Start(explicitlyUnrankedTest);
    }

    private void SaveAutomationHistory()
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-automation-activity.txt");
            controller.SaveHistory(path);
            AddEvent("Saved automation activity log: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save automation activity log");
            AddEvent("Automation activity log failed: " + ex.Message);
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var argument = arguments.Trim();
        if (argument.StartsWith("auto ", StringComparison.OrdinalIgnoreCase))
        {
            switch (argument.Substring(5).Trim().ToLowerInvariant())
            {
                case "start": StartAutomation(); break;
                case "pause": controller.Pause("User command"); break;
                case "resume": controller.Resume(explicitlyUnrankedTest); break;
                case "stop": controller.Stop("User command"); break;
                default: AddEvent("Usage: /mahint auto start|pause|resume|stop"); break;
            }
            return;
        }
        if (argument.StartsWith("addon ", StringComparison.OrdinalIgnoreCase))
        {
            SaveNamedAddon(argument.Substring(6).Trim());
            return;
        }
        switch (argument.ToLowerInvariant())
        {
            case "off": Capture(false); break;
            case "on": Capture(true); break;
            case "diff": WriteDiff(); break;
            case "hint": SaveLiveHint(); break;
            case "hand": SaveHandScan(); break;
            case "call": SaveCallPrompt(); break;
            case "riichi": SaveRiichiPrompt(); break;
            case "chi": SaveChiChoices(); break;
            case "addons": SaveVisibleAddons(); break;
            case "results": SaveRoundResults(); break;
            case "observe": observeChanges = !observeChanges; AddEvent("Observe changes = " + observeChanges); break;
            case "stop": observeChanges = false; AddEvent("Observation stopped"); break;
            default: windowOpen = !windowOpen; break;
        }
    }

    private void Draw()
    {
        // Automated in-match actions run only after explicit Start.
        controller.Tick(explicitlyUnrankedTest);
        if (DateTime.UtcNow >= nextPollUtc)
        {
            nextPollUtc = DateTime.UtcNow.AddMilliseconds(400);
            Poll();
        }
        if (!windowOpen) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(660, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Doman Hint Pilot v0.8.19 (in-round only)", ref windowOpen))
        {
            ImGui.End();
            return;
        }
        ImGui.TextUnformatted("Continuous play (single match; NO MATCHMAKING OR REQUEUE)");
        if (ImGui.Checkbox("I am testing at an NPC / unranked casual table", ref explicitlyUnrankedTest))
        {
            if (!explicitlyUnrankedTest) controller.Pause("NPC/unranked acknowledgment removed");
            ClearPendingDiscard(); ClearPendingResponse();
            ClearPendingChiChoice(); ClearAutoChi();
        }
        if (ImGui.Checkbox("Allow experimental automatic Chi (unique hint match only)", ref autoAllowChi))
        {
            controller.AllowChi = autoAllowChi;
            if (controller.Running && !controller.Paused) controller.Pause("Chi policy changed; review before resuming");
        }
        if (ImGui.Checkbox("Allow experimental Kan (hint + visible Kan option only)", ref autoAllowKan))
        {
            controller.AllowKan = autoAllowKan;
            if (controller.Running && !controller.Paused)
                controller.Pause("Kan policy changed; review before resuming");
        }
        if (!controller.Running)
        {
            if (ImGui.Button("START in-match automation")) StartAutomation();
        }
        else if (controller.Paused)
        {
            if (ImGui.Button("RESUME automation")) controller.Resume(explicitlyUnrankedTest);
            ImGui.SameLine();
            if (ImGui.Button("STOP automation")) controller.Stop("User pressed Stop");
        }
        else
        {
            if (ImGui.Button("PAUSE automation")) controller.Pause("User pressed Pause");
            ImGui.SameLine();
            if (ImGui.Button("STOP automation")) controller.Stop("User pressed Stop");
        }
        ImGui.TextWrapped("  Automation status: " + controller.Status);
        ImGui.TextUnformatted("  Actions sent this session: " + controller.ActionCount);
        if (ImGui.Button("Save automation activity log")) SaveAutomationHistory();
        ImGui.TextWrapped("Automates in-round hints only. Round Next and End Match are NEVER clicked " +
            "by this plugin; use the game controls or let its timer advance. " +
            "Chi/Kan are experimental. Matchmaking and requeue are manual.");
        ImGui.Separator();
        ImGui.TextUnformatted("Current Mahjong addon: " + tableName);
        ImGui.TextUnformatted("Mahjong AtkValues[0] state: " + (stateCode < 0 ? "n/a" : stateCode.ToString()));
        ImGui.Separator();
        ImGui.TextUnformatted("Live game hint (read-only):");
        if (liveHint.IsDiscardHint)
        {
            ImGui.TextUnformatted("  Recommended: " + liveHint.TileLabel);
            ImGui.TextUnformatted("  Icon ID: " + liveHint.IconId + "  |  Action: " + liveHint.Action);
            ImGui.TextUnformatted("  Icon source: " + liveHint.Source);
        }
        else
        {
            ImGui.TextWrapped(liveHint.PanelVisible &&
                    int.TryParse(liveHint.Action.Trim(), out _)
                ? "  Raw numeric UI payload ('" + liveHint.Action + "'), NOT a named gameplay hint. Waiting for a playable recommendation."
                : liveHint.PanelVisible && !string.IsNullOrWhiteSpace(liveHint.Action)
                ? "  Current hint is an action ('" + liveHint.Action + "'), not a discard. See call prompt below."
                : "  No discard hint. Wait for your turn or enable in-game Hints.");
            ImGui.TextUnformatted("  Panel visible: " + liveHint.PanelVisible +
                "  |  Raw icon: " + liveHint.IconId + "  |  Action: " + liveHint.Action);
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Call prompt / response options (experimental):");
        ImGui.TextUnformatted("  In-game hint: " +
            (callPrompt.HintVisible && !string.IsNullOrWhiteSpace(callPrompt.RecommendedAction)
                ? callPrompt.RecommendedAction : "(none visible)"));
        ImGui.TextWrapped("  Available actions: " + callPrompt.Summary);
        if (callPrompt.State == 15)
            ImGui.TextUnformatted("  Mahjong call state: 15 (opponent response pending)");
        else if (callPrompt.IsVerifiedRiichiOffer)
            ImGui.TextUnformatted($"  RIICHI ready: state {callPrompt.State} / visible Riichi / Pass choices");
        else if (callPrompt.HasVerifiedTsumoOffer)
            ImGui.TextUnformatted("  TSUMO ready: state 6 / exact visible Tsumo! hint. Manual test only.");
        ImGui.Separator();
        if (callPrompt.State == 6 && callPrompt.HintVisible &&
            callPrompt.RecommendedAction.Contains("Riichi", StringComparison.OrdinalIgnoreCase) &&
            !callPrompt.HasRiichiPassOffer)
        {
            ImGui.TextWrapped("Riichi diagnostic: state 6 still shows a Riichi hint, but the exact " +
                "active Riichi/Pass controls were NOT verified. This may be post-Riichi discard. " +
                "If Riichi/Pass is visibly offered, run /mahint riichi and upload the capture.");
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Hand tile scan (experimental, read-only):");
        ImGui.TextWrapped(handScan.Summary);
        if (handScan.MatchingSlots.Count > 0)
            ImGui.TextUnformatted("Matching hand positions (1-based): " +
                string.Join(", ", handScan.MatchingSlots.Select(x => (x + 1).ToString())));
        if (ImGui.CollapsingHeader("Show all 14 hand positions"))
        {
            foreach (var tile in handScan.Tiles)
            {
                string marker = handScan.MatchingSlots.Contains(tile.Slot) ? "  <= MATCH" : "";
                ImGui.TextUnformatted($"{tile.Slot + 1:00}  {tile.TileLabel} [{tile.IconId}]" + marker);
                if (tile.IconId < 0)
                    ImGui.TextDisabled("       " + tile.Detail);
            }
        }
        ImGui.TextWrapped("The tile reader is passive unless the controller is explicitly RUNNING. " +
            "Manual Chi confirmation may also arm the separate one-shot meld watcher.");
        ImGui.Separator();
        ImGui.TextUnformatted("Experimental manual actions: responses FIRST, discards SECOND");
        ImGui.TextWrapped("NPC or unranked casual table ONLY; not Quick Ranked Match. " +
            "The Chi call needs Prepare/Confirm; an optional unique hint-matching meld can follow without another click.");
        ImGui.TextDisabled("Manual buttons below are for testing or recovery; pause automation first.");
        if (ImGui.Checkbox("Manual Riichi-first override (ignores Discard hint)", ref prioritizeRiichi))
        {
            ClearPendingDiscard();
            ClearPendingResponse();
        }
        if (callPrompt.HasVerifiedTsumoOffer)
            ImGui.TextWrapped("TSUMO FIRST: the hint recommends a self-draw win. " +
                "Do not discard the drawn tile. Tsumo [11,0] was verified in a manual test.");
        if (callPrompt.HasRiichiPassOffer)
        {
            ImGui.TextWrapped(prioritizeRiichi
                ? "RIICHI FIRST: Riichi / Pass is present. Prepare/Confirm a Riichi response " +
                  "before discarding. This can OVERRIDE a game hint recommending Discard. " +
                  "After declaring Riichi, prepare/confirm the indicated discard."
                : "Riichi / Pass is available. Automatic gameplay follows the hint: " +
                  "Riichi only when recommended; otherwise Pass then discard. " +
                  "Manual Riichi-first override is OFF.");
        }
        ImGui.Separator();
        ImGui.TextUnformatted("STEP 1: Prepare/confirm an offered response (game hint controls automatic play)");
        ImGui.TextWrapped("Opponent calls (state 15): Pass, Chi, Pon, Kan, Ron. " +
            "At state 30, or state 6 with verified visible choices, Riichi-first requires " +
            "exactly Riichi (option 0) / Pass (option 1). " +
            "State 6 + exact visible Tsumo! hint prepares the previously tested " +
            "Tsumo (option 0). This section provides manual testing controls.");
        if (ImGui.Button("1. Prepare response (manual override optional)")) PrepareRecommendedResponse();
        if (pendingResponseOption >= 0 && DateTime.UtcNow <= pendingCallUntilUtc)
        {
            ImGui.TextWrapped($"Prepared: {pendingResponseLabel} (option {pendingResponseOption}). " +
                (pendingRiichiPriority ? "Riichi-first override. " : "Game-recommended response. ") +
                "Expires after 6 seconds.");
            if (ImGui.Button("2. CONFIRM response exactly once")) ConfirmRecommendedResponse();
            ImGui.SameLine();
            if (ImGui.Button("Cancel prepared response")) ClearPendingResponse();
        }
        else if (pendingResponseOption >= 0)
        {
            ClearPendingResponse();
            ImGui.TextDisabled("Prepared response expired. Prepare again if still offered.");
        }
        ImGui.Separator();
        ImGui.TextUnformatted("STEP 1B: Hint-matched Chi meld selection (experimental)");
        ImGui.TextWrapped("After you manually confirm Chi, the plugin can choose LEFT or RIGHT " +
            "by exactly matching the THREE recommended tiles in the hint panel. " +
            "Red fives remain distinct. It does not pick an unmatched or ambiguous option.");
        if (ImGui.Checkbox("Select unique hint-matching Chi meld after confirmed Chi", ref autoChiHintMatch))
            ClearAutoChi();
        ImGui.TextWrapped("  Meld chooser: " + ChiMeldChoiceTest.Read(GameGui));
        if (ChiMeldChoiceTest.TryGetHintMatch(GameGui, out _, out var recommendedSide, out var matchDetail))
            ImGui.TextWrapped("  Hint match: " + recommendedSide + " (unverified action)");
        else if (ChiMeldChoiceTest.TryGetIdentity(GameGui, out _, out _))
            ImGui.TextWrapped("  Hint match blocked: " + matchDetail);
        if (autoChiArmed)
            ImGui.TextWrapped("  Auto-Chi armed after confirmed Chi: " + autoChiLastReason +
                " (expires in " + Math.Max(0, (autoChiUntilUtc - DateTime.UtcNow).TotalSeconds).ToString("F1") + "s)");
        if (ImGui.CollapsingHeader("Manual Chi fallback (Left / Right)"))
        {
            ImGui.TextWrapped("Use only if automatic matching was blocked. Check the displayed tiles first.");
            if (ImGui.Button("1. Prepare LEFT Chi combination")) PrepareChiChoice(ChiMeldChoiceTest.Side.Left);
            if (ImGui.Button("1. Prepare RIGHT Chi combination")) PrepareChiChoice(ChiMeldChoiceTest.Side.Right);
            if (pendingChiChoice && pendingChiIdentity is not null && DateTime.UtcNow <= pendingChiUntilUtc)
            {
                ImGui.TextWrapped($"Prepared {pendingChiSide} Chi meld. Verify the tiles on screen; expires in 8s.");
                if (ImGui.Button($"2. CONFIRM {pendingChiSide} Chi combination ONCE")) ConfirmChiChoice();
                ImGui.SameLine();
                if (ImGui.Button("Cancel prepared Chi combination")) ClearPendingChiChoice();
            }
            else if (pendingChiChoice)
            {
                ClearPendingChiChoice();
                ImGui.TextDisabled("Prepared Chi choice expired; prepare again if still offered.");
            }
        }
        if (ImGui.Button("Inspect Chi meld choices (read-only)")) SaveChiChoices();
        ImGui.Separator();
        ImGui.TextUnformatted("STEP 2: Prepare/confirm the recommended discard");
        ImGui.TextWrapped("After Riichi is accepted, the state-6 Riichi!/Discard hint is handled " +
            "as before. Kan/Pass discards are also unaffected.");
        if (autoChiArmed)
            ImGui.TextDisabled("Discard blocked: waiting briefly for the just-confirmed Chi choice.");
        else if (callPrompt.HasVerifiedTsumoOffer)
            ImGui.TextDisabled("Discard blocked: Tsumo! is offered. Resolve the win response first.");
        else if (callPrompt.HasRiichiPassOffer)
            ImGui.TextDisabled("Discard blocked: resolve Riichi/Pass first (hint-led auto play chooses Pass when Discard is advised).");
        else if (ImGui.Button("1. Prepare recommended discard")) PrepareDiscard();
        if (pendingSlot >= 0 && DateTime.UtcNow <= pendingUntilUtc)
        {
            ImGui.TextWrapped($"Prepared: {AddonSnapshot.IconToKnownTile(pendingIcon)}, " +
                $"position {pendingSlot + 1} (slot {pendingSlot}). Expires after 10 seconds.");
            if (ImGui.Button("2. CONFIRM discard exactly once")) ConfirmDiscard();
            ImGui.SameLine();
            if (ImGui.Button("Cancel prepared discard")) ClearPendingDiscard();
        }
        else if (pendingSlot >= 0)
        {
            ClearPendingDiscard();
            ImGui.TextDisabled("Prepared discard expired. Prepare again if needed.");
        }
        ImGui.TextWrapped("Manual Prepare/Confirm remains available; using it pauses any running controller. No queueing or requeueing.");
        ImGui.Separator();
        ImGui.TextWrapped("Round results and End Match are handled in FFXIV. " +
            "This plugin only observes those transitions; it sends no Next or End Match input.");
        if (ImGui.Button("Save visible addon list (including match results)")) SaveVisibleAddons();
        if (ImGui.Button("Save round-results diagnostic (read-only)")) SaveRoundResults();
        if (ImGui.Button("Save current call prompt to file")) SaveCallPrompt();
        if (ImGui.Button("Diagnose Riichi / Pass (read-only)")) SaveRiichiPrompt();
        if (ImGui.Button("Save hand scan to file")) SaveHandScan();
        if (ImGui.Button("Save current hint to file")) SaveLiveHint();
        ImGui.Separator();
        if (ImGui.Button("1. Capture with hints OFF")) Capture(false);
        ImGui.SameLine();
        if (ImGui.Button("2. Capture with hints ON")) Capture(true);
        if (ImGui.Button("3. Compare captures")) WriteDiff();
        ImGui.SameLine();
        if (ImGui.Button(observeChanges ? "Stop observing" : "Observe status changes"))
            observeChanges = !observeChanges;
        ImGui.TextWrapped("Last action: " + lastStatus);
        ImGui.TextWrapped("Snapshot folder: " + snapshotsDirectory);
        ImGui.Separator();
        ImGui.TextUnformatted("Recent events:");
        foreach (var item in events) ImGui.TextWrapped(item);
        ImGui.End();
    }

    private void Poll()
    {
        // A small read-only peek; full snapshots are captured only on demand.
        var current = AddonSnapshot.ReadCurrentHint(GameGui);
        var changed = current.Addon != liveHint.Addon || current.State != liveHint.State ||
            current.IconId != liveHint.IconId || current.Action != liveHint.Action ||
            current.PanelVisible != liveHint.PanelVisible;
        liveHint = current;
        handScan = HandTileScanner.Read(GameGui, current);
        callPrompt = CallPromptReader.Read(GameGui);
        if (!controller.Running || controller.Paused) ProcessAutoChi();
        // If an offered Riichi takes priority, never leave a previously
        // prepared discard pending as the prompt becomes visible.
        if ((callPrompt.HasVerifiedTsumoOffer ||
             prioritizeRiichi && callPrompt.HasRiichiPassOffer) && pendingSlot >= 0)
            ClearPendingDiscard();
        tableName = current.Addon;
        stateCode = current.State;
        if (changed && observeChanges)
            AddEvent($"Table={current.Addon}, state={current.State}, " +
                $"panel={current.PanelVisible}, action={current.Action}, " +
                $"icon={current.IconId} ({current.TileLabel})");
    }


    private static string Fingerprint(HandScan scan) =>
        string.Join(",", scan.Tiles.Select(t => t.IconId.ToString()));

    private void ClearPendingDiscard()
    {
        pendingHandFingerprint = null;
        pendingIcon = -1;
        pendingSlot = -1;
        pendingUntilUtc = DateTime.MinValue;
    }

    private bool TryReadVerifiedDiscard(out HintReading currentHint, out HandScan currentHand)
    {
        currentHint = AddonSnapshot.ReadCurrentHint(GameGui);
        currentHand = HandTileScanner.Read(GameGui, currentHint);
        if (!explicitlyUnrankedTest)
        {
            AddEvent("Action blocked: first acknowledge NPC/unranked-casual testing.");
            return false;
        }
        if (currentHint.Addon != "Emj" && currentHint.Addon != "EmjL")
        {
            AddEvent("Action blocked: no Mahjong table.");
            return false;
        }
        // Always check the real-time prompt rather than the 400-ms UI cache.
        // This guard runs on BOTH prepare and confirm so a late Riichi offer
        // cannot be bypassed by an already prepared discard.
        var livePrompt = CallPromptReader.Read(GameGui);
        if (livePrompt.HasVerifiedTsumoOffer)
        {
            AddEvent("Discard blocked: the live UI shows Tsumo!; resolve the winning response instead.");
            return false;
        }
        if (prioritizeRiichi && livePrompt.HasRiichiPassOffer)
        {
            AddEvent("Discard blocked: Riichi / Pass is offered and Riichi-first is enabled. " +
                "Prepare/Confirm Riichi before discarding, or turn the priority option off.");
            return false;
        }
        if ((currentHint.State != 6 && currentHint.State != 30) || !currentHint.IsDiscardHint)
        {
            AddEvent($"Action blocked: no active discard hint at state {currentHint.State}.");
            return false;
        }
        if (!currentHand.FullHandRead || currentHand.Tiles.Count != 14 || currentHand.MatchingSlots.Count == 0)
        {
            AddEvent("Action blocked: hand incomplete or no matching tile.");
            return false;
        }
        return true;
    }

    private void PrepareDiscard()
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual PrepareDiscard requested");
        ClearAutoChi();
        ClearPendingDiscard();
        ClearPendingChiChoice();
        if (!TryReadVerifiedDiscard(out var hint, out var scan)) return;
        // Duplicates have the same icon; a consistent first slot is preferred.
        pendingIcon = hint.IconId;
        pendingSlot = scan.MatchingSlots[0];
        pendingHandFingerprint = Fingerprint(scan);
        pendingUntilUtc = DateTime.UtcNow.AddSeconds(10);
        AddEvent($"Prepared {hint.TileLabel} at slot {pendingSlot}. Click Confirm to send one discard.");
    }

    private unsafe void ConfirmDiscard()
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual ConfirmDiscard requested");
        try
        {
            if (pendingSlot < 0 || pendingHandFingerprint == null || DateTime.UtcNow > pendingUntilUtc)
            {
                AddEvent("Action blocked: no valid prepared discard.");
                ClearPendingDiscard();
                return;
            }
            int intendedIcon = pendingIcon;
            int intendedSlot = pendingSlot;
            string intendedFingerprint = pendingHandFingerprint;
            ClearPendingDiscard(); // consume before any action to prevent repeats
            if (!TryReadVerifiedDiscard(out var hint, out var scan)) return;
            if (hint.IconId != intendedIcon || Fingerprint(scan) != intendedFingerprint ||
                !scan.MatchingSlots.Contains(intendedSlot) || scan.Tiles[intendedSlot].IconId != intendedIcon)
            {
                AddEvent("Action blocked: hint or hand changed since Prepare.");
                return;
            }
            var addon = GameGui.GetAddonByName<AtkUnitBase>(hint.Addon);
            if (addon == null || !addon->IsVisible)
            {
                AddEvent("Action blocked: Mahjong UI no longer visible.");
                return;
            }

            // Provisional protocol from MahjongXIV live-verification document.
            // Verify against manually captured callbacks on your client before relying on it.
            // No loops, delays, retries, or follow-up inputs.
            var args = stackalloc AtkValue[2];
            args[0] = new AtkValue { Type = AtkValueType.Int, Int = 15 };
            args[1] = new AtkValue { Type = AtkValueType.Int, Int = intendedIcon };
            bool highlightReturned = addon->FireCallback(2, args);
            args[0].Int = 7;
            args[1].Int = intendedSlot;
            bool commitReturned = addon->FireCallback(2, args);
            AddEvent($"One-shot callbacks issued: [15,{intendedIcon}] -> [7,{intendedSlot}] " +
                $"(return values {highlightReturned}/{commitReturned}). Confirm tile actually left in-game.");
        }
        catch (Exception e)
        {
            Log.Error(e, "Experimental manual discard test failed");
            AddEvent("Experimental discard test threw " + e.GetType().Name + ": " + e.Message);
        }
    }

    private void ClearPendingResponse()
    {
        pendingCallSignature = null;
        pendingRiichiPriority = false;
        pendingResponseOption = -1;
        pendingResponseLabel = null;
        pendingCallUntilUtc = DateTime.MinValue;
    }

    private bool TryReadVerifiedResponse(out CallPrompt prompt, bool allowRiichiPriority = false)
    {
        prompt = CallPromptReader.Read(GameGui);
        if (!explicitlyUnrankedTest)
        {
            AddEvent("Response blocked: first acknowledge NPC/unranked-casual testing.");
            return false;
        }
        // A distinct, explicitly opted-in Riichi-first path. It does NOT
        // assume that Discard is a Riichi recommendation: it requires exact
        // Riichi/Pass button labels, their verified option indices, and the
        // visible panel; user must still manually confirm callback [11,0].
        if (allowRiichiPriority && prioritizeRiichi &&
            prompt.HasRiichiPassOffer && prompt.HintVisible)
            return true;
        if (!prompt.CanOfferRecommendedResponse)
        {
            AddEvent($"Response blocked: state={prompt.State}, hint='{prompt.RecommendedAction}', " +
                $"visible={prompt.HintVisible}, options=[{prompt.Summary}]. " +
                "Expected state 15 with a recommended call, state 30 or verified state 6 with Riichi/Pass, " +
                "or state 6 with exact visible Tsumo! hint. " +
                "Capture /mahint call and /mahint hint if the displayed offer disagrees.");
            return false;
        }
        return true;
    }

    private void PrepareRecommendedResponse()
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual PrepareRecommendedResponse requested");
        ClearPendingResponse();
        ClearPendingChiChoice();
        ClearAutoChi();
        ClearPendingDiscard();
        if (!TryReadVerifiedResponse(out var prompt, allowRiichiPriority: true)) return;
        pendingRiichiPriority = prioritizeRiichi && prompt.HasRiichiPassOffer && prompt.HintVisible;
        pendingResponseOption = pendingRiichiPriority ? 0 : prompt.RecommendedOption;
        pendingResponseLabel = pendingRiichiPriority ? "Riichi" : prompt.NormalizedRecommendation;
        pendingCallSignature = prompt.Signature;
        pendingCallUntilUtc = DateTime.UtcNow.AddSeconds(6);
        AddEvent($"Prepared {pendingResponseLabel} at option {pendingResponseOption}" +
            (prompt.HasDiscardAsPassFallback ?
                " (visible Pon/Pass popup: Discard hint means decline the call)" : "") +
            (pendingRiichiPriority && !prompt.IsVerifiedRiichiOffer
                ? " (manual Riichi-first OVERRIDE of the game's hint)"
                : "") + "; confirm once while offered.");
    }

    private unsafe void ConfirmRecommendedResponse()
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual ConfirmRecommendedResponse requested");
        try
        {
            if (pendingResponseOption < 0 || pendingCallSignature == null ||
                pendingResponseLabel == null || DateTime.UtcNow > pendingCallUntilUtc)
            {
                AddEvent("Response blocked: no valid prepared action.");
                ClearPendingResponse();
                return;
            }
            int intendedOption = pendingResponseOption;
            string intendedSignature = pendingCallSignature;
            string intendedLabel = pendingResponseLabel;
            bool intendedRiichiPriority = pendingRiichiPriority;
            ClearPendingResponse(); // consume BEFORE any callback to prevent accidental repeats
            if (!TryReadVerifiedResponse(out var prompt, allowRiichiPriority: intendedRiichiPriority)) return;
            bool choiceStillValid = intendedRiichiPriority
                ? prioritizeRiichi && intendedOption == 0 &&
                  string.Equals(intendedLabel, "Riichi", StringComparison.OrdinalIgnoreCase) &&
                  prompt.HasRiichiPassOffer && prompt.HintVisible
                : prompt.RecommendedOption == intendedOption &&
                  string.Equals(prompt.NormalizedRecommendation, intendedLabel, StringComparison.OrdinalIgnoreCase);
            if (!choiceStillValid || prompt.Signature != intendedSignature)
            {
                AddEvent("Response blocked: game prompt, recommendation, or priority changed after Prepare.");
                return;
            }
            var addon = GameGui.GetAddonByName<AtkUnitBase>(prompt.Addon);
            if (addon == null || !addon->IsVisible)
            {
                AddEvent("Response blocked: table no longer visible.");
                return;
            }
            // Verified for Pass on user's unranked table; other response types are experimental.
            var args = stackalloc AtkValue[2];
            args[0] = new AtkValue { Type = AtkValueType.Int, Int = 11 };
            args[1] = new AtkValue { Type = AtkValueType.Int, Int = intendedOption };
            bool result = addon->FireCallback(2, args);
            if (string.Equals(intendedLabel, "Chi", StringComparison.OrdinalIgnoreCase) &&
                autoChiHintMatch && explicitlyUnrankedTest)
            {
                ClearAutoChi();
                autoChiArmed = true;
                autoChiUntilUtc = DateTime.UtcNow.AddSeconds(6);
                AddEvent("Confirmed Chi: armed one hint-matching meld selection for 6 seconds. " +
                    "No selection unless exactly one Chi option matches the visible hint.");
            }
            AddEvent($"One-shot {intendedLabel} issued [11,{intendedOption}] (callback returned {result}). " +
                (string.Equals(intendedLabel, "Riichi", StringComparison.OrdinalIgnoreCase)
                    ? "Now use Prepare/Confirm recommended discard once. The game handles later auto-discards."
                    : string.Equals(intendedLabel, "Tsumo", StringComparison.OrdinalIgnoreCase)
                        ? "Verify that the winning-hand results appear. Do not discard; Round Next still requires separate manual confirmation."
                        : string.Equals(intendedLabel, "Chi", StringComparison.OrdinalIgnoreCase)
                            ? (autoChiArmed ? "Waiting for unique hint-matching Chi meld; " +
                               "a single choice will be selected when stable." :
                               "If a meld choice appears, select manually or run /mahint chi.")
                            : "Verify the in-game result; handle any subsequent meld tile choice manually."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Experimental one-shot Mahjong response failed");
            AddEvent("Response callback threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }


    private void ClearAutoChi()
    {
        autoChiArmed = false;
        autoChiUntilUtc = DateTime.MinValue;
        autoChiObservedIdentity = null;
        autoChiObservedSide = null;
        autoChiFirstObservedUtc = DateTime.MinValue;
        autoChiLastReason = "Waiting for the Chi chooser.";
    }

    private void ProcessAutoChi()
    {
        if (!autoChiArmed) return;
        var now = DateTime.UtcNow;
        if (!explicitlyUnrankedTest || !autoChiHintMatch)
        {
            ClearAutoChi();
            return;
        }
        if (now > autoChiUntilUtc)
        {
            string detail = autoChiLastReason;
            ClearAutoChi();
            AddEvent("Auto-Chi expired without sending an event: " + detail +
                " Select manually if a choice is still visible, or capture /mahint chi.");
            return;
        }
        try
        {
            if (!ChiMeldChoiceTest.TryGetHintMatch(GameGui,
                    out var identity, out var side, out var reason))
            {
                autoChiLastReason = reason;
                autoChiObservedIdentity = null;
                autoChiObservedSide = null;
                autoChiFirstObservedUtc = DateTime.MinValue;
                return;
            }
            autoChiLastReason = reason;
            if (autoChiObservedIdentity is null || autoChiObservedIdentity.Value != identity ||
                autoChiObservedSide is null || autoChiObservedSide.Value != side)
            {
                autoChiObservedIdentity = identity;
                autoChiObservedSide = side;
                autoChiFirstObservedUtc = now;
                return;
            }
            // At least two stable polls, separated by ~400ms, plus an
            // independent final revalidation. The action is one-shot.
            if ((now - autoChiFirstObservedUtc).TotalMilliseconds < 300) return;
            ClearAutoChi(); // consume BEFORE dispatch: NEVER retry on failure
            if (ChiMeldChoiceTest.TryClickHintMatchOnce(GameGui, identity, side, out var detail))
            {
                ClearPendingChiChoice();
                AddEvent("One hint-matched Chi " + side + " event sent after confirmed Chi: " +
                    detail + ". Verify which meld was selected. No automatic retry.");
            }
            else
                AddEvent("Auto-Chi did not send an event: " + detail +
                    ". Choose manually or capture /mahint chi.");
        }
        catch (Exception ex)
        {
            ClearAutoChi();
            Log.Error(ex, "Hint-matched Chi selection failed");
            AddEvent("Auto-Chi stopped: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void ClearPendingChiChoice()
    {
        pendingChiChoice = false;
        pendingChiIdentity = null;
        pendingChiUntilUtc = DateTime.MinValue;
    }

    private void PrepareChiChoice(ChiMeldChoiceTest.Side side)
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual PrepareChiChoice requested");
        ClearAutoChi();
        ClearPendingChiChoice();
        ClearPendingDiscard();
        ClearPendingResponse();
        if (!explicitlyUnrankedTest)
        {
            AddEvent("Chi choice blocked: acknowledge NPC/unranked-casual testing first.");
            return;
        }
        try
        {
            if (!ChiMeldChoiceTest.TryGetIdentity(GameGui, out var identity, out var reason))
            {
                AddEvent("Chi choice not prepared: " + reason);
                return;
            }
            pendingChiChoice = true;
            pendingChiIdentity = identity;
            pendingChiSide = side;
            pendingChiUntilUtc = DateTime.UtcNow.AddSeconds(8);
            AddEvent($"Prepared {side} Chi combination. Compare the LEFT/RIGHT choices on the game screen; " +
                "confirm only your intended meld once within 8 seconds.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chi meld choice preparation failed");
            AddEvent("Chi choice prepare threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void ConfirmChiChoice()
    {
        if (controller.Running && !controller.Paused) controller.Pause("Manual ConfirmChiChoice requested");
        if (!pendingChiChoice || pendingChiIdentity is null ||
            DateTime.UtcNow > pendingChiUntilUtc)
        {
            ClearPendingChiChoice();
            AddEvent("Chi choice blocked: no unexpired prepared combination.");
            return;
        }
        var identity = pendingChiIdentity.Value;
        var side = pendingChiSide;
        ClearPendingChiChoice(); // consume before any dispatch; no retries
        if (!explicitlyUnrankedTest)
        {
            AddEvent("Chi choice blocked: NPC/unranked-casual acknowledgement unchecked.");
            return;
        }
        try
        {
            if (ChiMeldChoiceTest.TryClickOnce(GameGui, identity, side, out var detail))
                AddEvent("One Chi " + side + " meld event sent: " + detail + ". Verify the tiles changed correctly.");
            else
                AddEvent("Chi meld event not sent: " + detail);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Experimental Chi meld button event failed");
            AddEvent("Chi choice click threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void SaveRoundResults()
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-round-results-buttons.txt");
            File.WriteAllText(path, RoundResultsDiagnostics.Inspect(GameGui));
            AddEvent("Saved read-only round-results button diagnostic: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Round result button diagnostic failed");
            AddEvent("Could not inspect round results: " + ex.Message);
        }
    }

    private void SaveVisibleAddons()
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-visible-addons.txt");
            File.WriteAllText(path, ResultAddonDiagnostics.ListVisibleAddons());
            AddEvent("Saved visible addon list: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Visible addon capture failed");
            AddEvent("Could not scan visible addons: " + ex.Message);
        }
    }

    private void SaveNamedAddon(string name)
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-addon-detail.txt");
            File.WriteAllText(path, ResultAddonDiagnostics.InspectNamedAddon(GameGui, name));
            AddEvent("Saved read-only addon detail for '" + name + "': " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Addon detail capture failed");
            AddEvent("Could not inspect addon '" + name + "': " + ex.Message);
        }
    }

    private void SaveChiChoices()
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-chi-choices.txt");
            File.WriteAllText(path, ChiChoiceDiagnostics.Inspect(GameGui));
            AddEvent("Saved read-only Chi choice diagnostic: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Chi meld choice diagnostic failed");
            AddEvent("Could not inspect Chi choices: " + ex.Message);
        }
    }

    private void SaveRiichiPrompt()
    {
        try
        {
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-riichi-prompt.txt");
            File.WriteAllText(path, RiichiPromptDiagnostics.Inspect(GameGui));
            AddEvent("Saved focused Riichi/Pass UI diagnostic: " + Path.GetFileName(path));
        }
        catch (Exception e)
        {
            Log.Error(e, "Focused Riichi prompt diagnostic failed");
            AddEvent("Riichi diagnostic failed: " + e.Message);
        }
    }

    private void SaveCallPrompt()
    {
        try
        {
            var prompt = CallPromptReader.Read(GameGui);
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-call-prompt.txt");
            File.WriteAllLines(path, new[]
            {
                "# Doman Hint Pilot v0.7: passive call prompt diagnostic",
                "# No inputs were sent by this command",
                "Addon = " + prompt.Addon,
                "State = " + prompt.State,
                "HintPanelVisible = " + prompt.HintVisible,
                "RecommendedAction = " + prompt.RecommendedAction,
                "Options = " + prompt.Summary,
                "PassOption = " + prompt.PassOption,
                "RecommendedOption = " + prompt.RecommendedOption,
                "State15PonPassVisibleUI = " + prompt.VerifiedState15PonPassControls,
                "State15EmptyNoOffer = " + prompt.IsVerifiedEmptyState15,
                "State15BlankPanel = " + prompt.IsVerifiedBlankState15Panel,
                "DiscardMappedToPass = " + prompt.HasDiscardAsPassFallback,
                "EffectiveRecommendation = " + prompt.NormalizedRecommendation,
                "EligibleForManualRecommendedResponse = " + prompt.CanOfferRecommendedResponse,
                "HasVerifiedTsumoOffer = " + prompt.HasVerifiedTsumoOffer,
                "TsumoOption = " + (prompt.HasVerifiedTsumoOffer ? 0 : -1),
                "IsVerifiedRiichiOffer = " + prompt.IsVerifiedRiichiOffer,
                "HasRiichiPassOffer = " + prompt.HasRiichiPassOffer,
                "RiichiFirstEnabled = " + prioritizeRiichi,
                "RiichiFirstEligible = " +
                    (prioritizeRiichi && prompt.HasRiichiPassOffer && prompt.HintVisible),
            });
            AddEvent("Saved call prompt: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Call prompt diagnostic failed");
            AddEvent("Could not save call prompt: " + ex.Message);
        }
    }

    private void SaveLiveHint()
    {
        try
        {
            // Refresh immediately instead of relying on the last polling tick.
            liveHint = AddonSnapshot.ReadCurrentHint(GameGui);
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-live-hint.txt");
            File.WriteAllLines(path, new[]
            {
                "# Doman Hint Pilot: passive live hint capture",
                "# No game actions were sent",
                "Addon = " + liveHint.Addon,
                "State = " + liveHint.State,
                "HintPanelVisible = " + liveHint.PanelVisible,
                "Action = " + liveHint.Action,
                "RecommendedIconId = " + liveHint.IconId,
                "RecommendationSource = " + liveHint.Source,
                "KnownTileLabel = " + liveHint.TileLabel,
                "PlausibleDiscardHint = " + liveHint.IsDiscardHint,
                "# No hand-tile slot is identified by this capture",
            });
            AddEvent("Saved live hint: " + Path.GetFileName(path));
        }
        catch (Exception e)
        {
            Log.Error(e, "Doman Hint Pilot live hint capture failed");
            AddEvent("Live hint capture failed: " + e.Message);
        }
    }

    private void SaveHandScan()
    {
        try
        {
            var currentHint = AddonSnapshot.ReadCurrentHint(GameGui);
            handScan = HandTileScanner.Read(GameGui, currentHint);
            var path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-hand-scan.txt");
            File.WriteAllText(path, handScan.AsText());
            AddEvent("Saved hand scan: " + Path.GetFileName(path));
        }
        catch (Exception e)
        {
            Log.Error(e, "Doman Hint Pilot hand scan failed");
            AddEvent("Hand scan failed: " + e.Message);
        }
    }

    private void Capture(bool enabled)
    {
        try
        {
            var snapshot = AddonSnapshot.Take(GameGui);
            if (!snapshot.IsPresent)
            {
                AddEvent("No active Emj/EmjL addon. Enter a Mahjong table first.");
                return;
            }
            string label = enabled ? "hints-on" : "hints-off";
            string path = Path.Combine(snapshotsDirectory,
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + label + ".txt");
            File.WriteAllText(path, snapshot.AsText());
            if (enabled) hintsOn = snapshot; else hintsOff = snapshot;
            AddEvent($"Captured {label} ({snapshot.Entries.Count} entries): {Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            Log.Error(e, "Doman Hint Pilot capture failed");
            AddEvent("Capture failed: " + e.Message);
        }
    }

    private void WriteDiff()
    {
        if (hintsOff == null || hintsOn == null)
        {
            AddEvent("Capture BOTH hints OFF and hints ON before comparing.");
            return;
        }
        var path = Path.Combine(snapshotsDirectory,
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-hint-diff.txt");
        try
        {
            File.WriteAllText(path, AddonSnapshot.Compare(hintsOff, hintsOn));
            AddEvent("Saved hint diff: " + Path.GetFileName(path));
        }
        catch (Exception e)
        {
            Log.Error(e, "Doman Hint Pilot comparison failed");
            AddEvent("Could not save diff: " + e.Message);
        }
    }

    private void AddEvent(string message)
    {
        lastStatus = message;
        events.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        while (events.Count > 20) events.Dequeue();
        Log.Information("[DomanHintPilot] " + message);
    }

    public void Dispose()
    {
        controller.Stop("Plugin unloaded");
        ClearPendingDiscard();
        ClearPendingResponse();
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        CommandManager.RemoveHandler("/mahint");
    }
}
