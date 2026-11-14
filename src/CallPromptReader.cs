using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>A visible call/self-action button and its [11, option] index.</summary>
public readonly record struct CallOption(int Option, string Label);

/// <summary>
/// Passive reading of the currently offered call buttons. An empty or unclear
/// option list MUST NOT be interpreted as permission to send an input.
/// </summary>
public sealed record CallPrompt(
    string Addon,
    int State,
    string RecommendedAction,
    bool HintVisible,
    IReadOnlyList<CallOption> Options,
    bool VerifiedTsumoHint = false,
    bool VerifiedState6RiichiControls = false,
    bool VerifiedState15PonPassControls = false,
    bool VerifiedEmptyState15NoOffer = false,
    bool VerifiedBlankState15Panel = false,
    bool VerifiedNumericTripleState15Frame = false,
    bool VerifiedState15RiichiPassNumericDiscard = false,
    bool VerifiedHiddenState15ChoiceList = false)
{
    public static CallPrompt None { get; } = new("(none)", -1, "", false, Array.Empty<CallOption>());

    public int PassOption
    {
        get
        {
            var matches = Options.Where(o => string.Equals(o.Label.Trim(), "Pass", StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? matches[0].Option : -1;
        }
    }

    // State 15: opponent calls; state 30: existing Riichi/Pass offer.
    // State 6 has TWO separately verified paths: the exact visible Tsumo!
    // self-draw hint OR exact Riichi/Pass labels plus the visible choice list.
    // Neither a Riichi!/Discard card alone nor numeric AtkValues authorize Riichi.
    private static string NormalizeAction(string value) =>
        value.Trim().TrimEnd('!', '！', '.', '。').Trim();

    // When the opponent's state-15 response popup visibly offers Pon/Pass,
    // the game's general hint may read "Discard" even though no discard is
    // possible until the popup is resolved. Declining that offer is "Pass".
    // This translation is allowed ONLY when the exact two clickable Pon/Pass
    // controls were identified from the visible UI; it never authorizes a
    // discard or a Pon call based on an unrelated announcement such as "Pon!".
    // The 14:34:00 snapshot: state 15, typed numeric zero in action and
    // initial options, [5]=-1, [8]=Undefined, and NO active response rows.
    // This is waiting/transitioning, not an actionable prompt. A visible
    // unknown chooser or a different set of values is never classified idle.
    public bool IsVerifiedEmptyState15 => State == 15 &&
        VerifiedEmptyState15NoOffer && Options.Count == 0;

    // 14:57 capture: state 15 / Discard, zero numeric response fields,
    // [5]=1, and a visible outer choice panel with no active choice rows.
    // This ONLY permits a short, bounded wait. It is never a Pass/discard
    // authorization, and never becomes an actionable CallPrompt.
    public bool IsVerifiedBlankState15Panel => State == 15 &&
        VerifiedBlankState15Panel && Options.Count == 0;

    // 15:03:08 capture: state 15, [6]=29 as an INTEGER, [7]=3,
    // [8..10] = the same valid tile icon, and no active choice rows.
    // The numbers do not identify an authorized Pon, Kan, Pass, or discard.
    // This classification permits only a bounded, input-free observation.
    public bool IsVerifiedNumericTripleState15Frame => State == 15 &&
        VerifiedNumericTripleState15Frame && Options.Count == 0;

    // Captured at 15:09:35 AFTER the automation attempted Riichi and then
    // discarded: state 15 / Discard / numeric zero option block, yet the
    // visible choice list still reads Riichi / Pass. This is NOT evidence
    // that either option should be clicked again. The controller alone
    // decides whether a prior Riichi attempt permits a bounded passive wait.
    public bool HasUnresolvedState15RiichiPassUi => State == 15 &&
        VerifiedState15RiichiPassNumericDiscard && Options.Count == 0;

    // The game retains old response row labels (including Chi / Pass) after
    // the enclosing choice LIST becomes hidden. These are stale, not buttons.
    // State 15 with that exact hidden list is a passive transition, even when
    // the hint still says Discard or numeric 0. No response is authorized.
    public bool IsVerifiedHiddenState15ChoiceList => State == 15 &&
        VerifiedHiddenState15ChoiceList;

    public bool HasDiscardAsPassFallback =>
        State == 15 && VerifiedState15PonPassControls && HintVisible &&
        Options.Count == 2 && Options[0].Option == 0 &&
        Options[0].Label == "Pon" && Options[1].Option == 1 &&
        Options[1].Label == "Pass" &&
        string.Equals(NormalizeAction(RecommendedAction), "Discard", StringComparison.OrdinalIgnoreCase);

    public string NormalizedRecommendation => HasDiscardAsPassFallback
        ? "Pass" : NormalizeAction(RecommendedAction);

    public int RecommendedOption
    {
        get
        {
            // Tsumo/Pass on the self-draw surface has numeric AtkValues instead
            // of readable option strings. The game UI captured in v0.7.9 shows
            // state 6 with exact visible "Tsumo!". A reference implementation
            // expects [11,0]; the user subsequently verified it manually.
            if (HasVerifiedTsumoOffer) return 0;
            string recommendation = NormalizedRecommendation;
            bool allowed = State == 15
                ? new[] { "Pass", "Chi", "Pon", "Kan", "Ron" }
                    .Any(x => string.Equals(x, recommendation, StringComparison.OrdinalIgnoreCase))
                : (State == 30 || (State == 6 && VerifiedState6RiichiControls)) &&
                  string.Equals(recommendation, "Riichi", StringComparison.OrdinalIgnoreCase);
            if (!allowed) return -1;
            var matches = Options.Where(o =>
                string.Equals(NormalizeAction(o.Label), recommendation, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matches.Count == 1 ? matches[0].Option : -1;
        }
    }

    public bool CanOfferRecommendedResponse =>
        (Addon == "Emj" || Addon == "EmjL") &&
        HintVisible &&
        !VerifiedHiddenState15ChoiceList &&
        RecommendedOption >= 0 &&
        (State == 15 && Options.Count >= 2 ||
         (State == 30 || State == 6) && IsVerifiedRiichiOffer ||
         HasVerifiedTsumoOffer);

    // The direct self-draw win uses state 6 without readable option labels.
    // Require a separately inspected visible UI text node that says "Tsumo!"
    // and an otherwise empty call-options list. No broad state-6 actuation.
    public bool HasVerifiedTsumoOffer => State == 6 && HintVisible &&
        VerifiedTsumoHint && Options.Count == 0 &&
        string.Equals(NormalizedRecommendation, "Tsumo", StringComparison.OrdinalIgnoreCase);

    // Exact visible self-action options. This is an *offer*, not necessarily
    // a game recommendation. The Riichi-first UI can choose it by explicit
    // user preference and confirmation even when the hint prefers Discard.
    // Never treat a Kan/Pass prompt or an ordinary discard as a Riichi offer.
    // State 6 must ALSO have live corroboration from the visible choices.
    // This distinguishes an active offer from the post-Riichi discard card.
    public bool HasRiichiPassOffer =>
        (State == 30 || (State == 6 && VerifiedState6RiichiControls)) &&
        Options.Count == 2 &&
        Options[0].Option == 0 &&
        string.Equals(NormalizeAction(Options[0].Label), "Riichi", StringComparison.OrdinalIgnoreCase) &&
        Options[1].Option == 1 &&
        string.Equals(NormalizeAction(Options[1].Label), "Pass", StringComparison.OrdinalIgnoreCase);

    // Preserve the strict game-recommended Riichi interpretation for the
    // existing non-override response reader and for diagnostics.
    public bool IsVerifiedRiichiOffer => HasRiichiPassOffer &&
        string.Equals(NormalizedRecommendation, "Riichi", StringComparison.OrdinalIgnoreCase);

    public bool CanOfferPass =>
        CanOfferRecommendedResponse &&
        string.Equals(NormalizedRecommendation, "Pass", StringComparison.OrdinalIgnoreCase);

    public string Signature => $"{Addon}|{State}|{RecommendedAction}|{HintVisible}|Tsumo={VerifiedTsumoHint}|State6Riichi={VerifiedState6RiichiControls}|" +
        $"State15PonPassUI={VerifiedState15PonPassControls}|State15RiichiPassNumeric={VerifiedState15RiichiPassNumericDiscard}|HiddenState15List={VerifiedHiddenState15ChoiceList}|EmptyState15={VerifiedEmptyState15NoOffer}|BlankState15={VerifiedBlankState15Panel}|NumericTripleState15={VerifiedNumericTripleState15Frame}|DiscardAsPass={HasDiscardAsPassFallback}|" +
        string.Join(";", Options.Select(o => $"{o.Option}:{o.Label}"));

    public string Summary => Options.Count == 0
        ? "No readable call options."
        : string.Join(" / ", Options.Select(o => $"{o.Label} (option {o.Option})"));
}

public static unsafe class CallPromptReader
{
    /// <summary>
    /// The existing game UI holds button labels at consecutive AtkValues from index 7.
    /// This is a candidate layout from MahjongXIV and needs live verification.
    /// Read only; never assume an undocumented option index when labels are missing.
    /// </summary>
    public static CallPrompt Read(IGameGui gameGui)
    {
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null || !addon->IsVisible) continue;
            if (addon->AtkValues == null || addon->AtkValuesCount < 8)
                return new CallPrompt(name, -1, "", false, Array.Empty<CallOption>());

            int state = addon->AtkValues[0].Int;
            string hintAction = "";
            try { hintAction = addon->AtkValues[6].GetValueAsString().Trim(); }
            catch { /* preserve an empty hint on read failure */ }

            var hintNode = addon->GetComponentNodeById(45);
            bool hintVisible = hintNode != null && ((AtkResNode*)hintNode)->IsVisible();
            // Do NOT promote a state-30 Discard recommendation to Riichi!
            // solely because both Riichi and Discard labels are present on
            // the hint card. Riichi availability is not a recommendation.
            // Preserve the actual AtkValue[6] action. The controller will
            // choose Pass for a verified Riichi/Pass offer if Discard is
            // recommended, and only declare Riichi for an explicit hint.
            // Prefer the active hint card's text to stale AtkValue[6] in
            // opponent state 15. Never use other call-announcement nodes.
            if (state == 15)
            {
                string cardAction = ReadVisibleHintCardAction(hintNode);
                if (cardAction.Length != 0) hintAction = cardAction;
            }
            // The self-action Kan hint can appear in state 6 while the raw
            // AtkValue[6] is numeric (observed 4). Read the EXACT visible
            // Kan! card instead of interpreting that integer as an action.
            // A concurrently visible Discard label disqualifies this path;
            // Kan availability alone must not override a Discard advice.
            // The controller independently requires a live Kan response row.
            if ((state is 6 or 30) && IsVisibleExclusiveKanHint(hintNode))
                hintAction = "Kan!";
            // State-6 Riichi from the captured live failure:
            // AtkValues[6]='Riichi!', [7]='Riichi', [8]='Pass', with
            // an ACTUAL visible choice list at component node 104.
            // Do not interpret a post-Riichi 'Riichi! / Discard' hint as an
            // offer unless those same option controls are still on screen.
            bool verifiedState6RiichiControls = state == 6 &&
                string.Equals(hintAction, "Riichi!", StringComparison.OrdinalIgnoreCase) &&
                IsVisibleRiichiPassChoices(addon);
            // Unlike Ron, Tsumo has numeric AtkValues[7..]. The exact visible
            // Tsumo! hint remains a separate manually verified path.
            bool verifiedTsumoHint = state == 6 && IsVisibleTsumoHint(hintNode);
            if (verifiedTsumoHint) hintAction = "Tsumo!";

            var options = new List<CallOption>();
            if (state == 15 || state == 30 || verifiedState6RiichiControls)
            {
                // Stop at the first non-string or empty label, matching the
                // contiguous popup option block; cap prevents unrelated reads.
                for (int i = 7; i < Math.Min((int)addon->AtkValuesCount, 15); ++i)
                {
                    var value = &addon->AtkValues[i];
                    if (!value->Type.ToString().Contains("String", StringComparison.Ordinal)) break;
                    string label;
                    try { label = value->GetValueAsString().Trim(); }
                    catch { break; }
                    if (label.Length == 0) break;
                    options.Add(new CallOption(i - 7, label));
                }
            }
            // v0.8.12: during end-of-round processing, old Chi/Pass
            // strings can survive in the option row components while their
            // parent list is HIDDEN. This is an observation-only state.
            // Evaluate it independently of [7..] string decoding so hidden
            // options can never be considered actionable.
            bool hiddenState15ChoiceList = state == 15 &&
                IsObservedHiddenState15ChoiceList(addon);
            // A real state-15 capture had numeric AtkValues[7..] and an
            // explicit visible Pon/Pass popup. Read that EXACT observed layout
            // rather than assuming there are no response choices. Never replace
            // partially decoded/string-based options with guessed indices.
            bool verifiedState15PonPassControls = state == 15 && hintVisible &&
                options.Count == 0 &&
                IsVisiblePonPassChoices(addon);
            if (verifiedState15PonPassControls)
            {
                options.Add(new CallOption(0, "Pon"));
                options.Add(new CallOption(1, "Pass"));
            }
            // 15:09:35 capture: even after the controller attempted Riichi,
            // state 15 showed the EXACT visible Riichi/Pass rows while the
            // regular response block was numeric (no string labels) and the
            // recommendation read Discard. Record this as an unresolved UI,
            // NOT as a second actionable Riichi offer or a Pass fallback.
            bool verifiedState15RiichiPass = state == 15 && hintVisible &&
                options.Count == 0 && !verifiedState15PonPassControls &&
                IsObservedState15RiichiPassNumericDiscard(addon);
            // Not every state-15 frame is a response. The user's snapshot
            // at 14:34:00 had all-zero numeric action/option fields and no
            // visible choice controls. This read-only classification permits
            // waiting, NOT sending Pass/Chi/Pon or any other input.
            bool verifiedEmptyState15 = state == 15 && options.Count == 0 &&
                !verifiedState15PonPassControls &&
                IsObservedEmptyState15WithoutChoices(addon);
            // Separate transient state-15 layout captured at 14:57: the
            // surrounding choice panel is visible but there are no active
            // readable response rows. The timer/timeout lives in AutoPlayController.
            bool verifiedBlankState15 = state == 15 && hintVisible &&
                options.Count == 0 && !verifiedState15PonPassControls &&
                !verifiedEmptyState15 && IsObservedBlankState15Panel(addon);
            // The 15:03:08 capture had a numeric action value 29, not a
            // readable call name. Accept only its precise, NON-actionable
            // signature when none of the other supported readers matched.
            bool verifiedNumericTriple = state == 15 && hintVisible &&
                options.Count == 0 && !verifiedState15PonPassControls &&
                !verifiedEmptyState15 && !verifiedBlankState15 &&
                IsObservedNumericTripleState15Frame(addon);
            return new CallPrompt(name, state, hintAction, hintVisible, options,
                verifiedTsumoHint, verifiedState6RiichiControls,
                verifiedState15PonPassControls, verifiedEmptyState15,
                verifiedBlankState15, verifiedNumericTriple,
                verifiedState15RiichiPass, hiddenState15ChoiceList);
        }
        return CallPrompt.None;
    }

    /// <summary>
    /// On the captured failed state-6 Riichi offer, the visible choice list is
    /// Root/228 id104 (1052) -> child000 id3 (1030) ->
    /// child000 id2 (1029) 'Riichi', child001 id21001 (1029) 'Pass'.
    /// These precise controls MUST be present, visible and mouse-enabled;
    /// the hint card alone is not evidence that Riichi can still be declared.
    /// Read-only; no event dispatch is performed here.
    /// </summary>
    /// <summary>
    /// v0.8.4 auto-pause capture: state 15, numeric AtkValues[7..], but
    /// Root/228 (node104) -> choice list -> clickable Pon (id2) and Pass
    /// (id21001), with both labels visible. This fallback is deliberately
    /// restricted to that exact two-option layout. Unknown layouts still pause.
    /// No UI events are dispatched here.
    /// </summary>
    /// <summary>
    /// Strict read-only classification of the 14:34:00 state-15 capture.
    /// A dormant hint panel or old score labels (Pon!/Tsumo!) do not imply
    /// an active response. If the choice component is missing, malformed,
    /// or any enabled choice row is visible, return false (caller pauses).
    /// Nothing is clicked from this method.
    /// </summary>
    /// <summary>
    /// Read-only, deliberately narrow end-of-round transition classification.
    /// The 2026-11-13 capture had node 104 visible while its child choice
    /// LIST (id 3, type 1030) was hidden. Chi and Pass text remained in the
    /// row components, but hidden ancestors make them non-actionable.
    /// 2026-11-13 14:09 snapshot: action was numeric 22 while the Chi/Pass
    /// choice rows were still present beneath an INVISIBLE parent list.
    /// Therefore AtkValues[6] is deliberately irrelevant for deciding
    /// whether that menu is actionable. A visible choice list or malformed
    /// tree must not be classified as a passive wait by this check.
    /// </summary>
    private static bool IsObservedHiddenState15ChoiceList(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady || !addon->IsVisible ||
                addon->AtkValues == null || addon->AtkValuesCount < 7) return false;
            var v = addon->AtkValues;
            if (v[0].Type != AtkValueType.Int || v[0].Int != 15) return false;
            // Do not inspect AtkValues[6] here: it is a payload/transition
            // field as well as a hint, and captured inactive states have
            // contained numeric 0, 22, Discard, and other values. A *hidden*
            // list cannot offer a button regardless of that payload. Only
            // the actual list visibility establishes this passive wait.
            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null) return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            return listNode != null && listNode->NodeId == 3 &&
                (int)listNode->Type == 1030 && !listNode->IsVisible();
        }
        catch { return false; }
    }

    private static bool IsObservedEmptyState15WithoutChoices(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady || !addon->IsVisible ||
                addon->AtkValues == null || addon->AtkValuesCount < 9) return false;
            var v = addon->AtkValues;
            if (v[0].Type != AtkValueType.Int || v[0].Int != 15 ||
                v[5].Type != AtkValueType.Int || v[5].Int != -1 ||
                v[6].Type != AtkValueType.Int || v[6].Int != 0 ||
                v[7].Type != AtkValueType.Int || v[7].Int != 0 ||
                !string.Equals(v[8].Type.ToString(), "Undefined", StringComparison.Ordinal))
                return false;
            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052) return false;
            // A completely hidden response panel has no clickable offer.
            if (!((AtkResNode*)group)->IsVisible()) return true;
            if (group->Component == null) return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            if (listNode == null || listNode->NodeId != 3 ||
                (int)listNode->Type != 1030) return false;
            if (!listNode->IsVisible()) return true;
            var list = ((AtkComponentNode*)listNode)->Component;
            if (list == null) return false;
            var manager = &list->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 2) return false;
            for (int i = 0; i < Math.Min(7, (int)manager->NodeListCount); ++i)
            {
                var row = manager->NodeList[i];
                if (row != null && (int)row->Type == 1029 && row->IsVisible() &&
                    row->NodeFlags.HasFlag(NodeFlags.Enabled) &&
                    row->NodeFlags.HasFlag(NodeFlags.RespondToMouse)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// EXACT 14:57:25 signature (not generic state-15 handling): [0]=15,
    /// [2]=3, [3]=a tile icon, [5]=1, [6]="Discard", [7..14]=zero Int,
    /// [15]=-1. Node104 outer panel may be visible while its choice list is
    /// hidden/inactive. If any visible enabled mouse-responsive choice row or
    /// any readable visible row label exists, this is NOT the blank signature.
    /// No callbacks are sent here. Unknown layouts still pause.
    /// </summary>
    private static bool IsObservedBlankState15Panel(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady || !addon->IsVisible ||
                addon->AtkValues == null || addon->AtkValuesCount < 16) return false;
            var v = addon->AtkValues;
            if (v[0].Type != AtkValueType.Int || v[0].Int != 15 ||
                v[2].Type != AtkValueType.Int || v[2].Int != 3 ||
                v[3].Type != AtkValueType.Int || v[3].Int < 76041 || v[3].Int > 76077 ||
                v[5].Type != AtkValueType.Int || v[5].Int != 1 ||
                !v[6].Type.ToString().Contains("String", StringComparison.Ordinal) ||
                !string.Equals(v[6].GetValueAsString().Trim(), "Discard", StringComparison.OrdinalIgnoreCase) ||
                v[15].Type != AtkValueType.Int || v[15].Int != -1) return false;
            for (int i = 7; i <= 14; i++)
                if (v[i].Type != AtkValueType.Int || v[i].Int != 0) return false;

            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null) return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            if (listNode == null || listNode->NodeId != 3 ||
                (int)listNode->Type != 1030) return false;
            if (!listNode->IsVisible()) return true;
            var list = ((AtkComponentNode*)listNode)->Component;
            if (list == null) return false;
            var manager = &list->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 2) return false;
            for (int i = 0; i < Math.Min(7, (int)manager->NodeListCount); i++)
            {
                var row = manager->NodeList[i];
                if (row == null || (int)row->Type != 1029 || !row->IsVisible()) continue;
                if (row->NodeFlags.HasFlag(NodeFlags.Enabled) &&
                    row->NodeFlags.HasFlag(NodeFlags.RespondToMouse)) return false;
                var component = ((AtkComponentNode*)row)->Component;
                if (component == null) return false;
                var children = &component->UldManager;
                if (children->NodeList == null || children->NodeListCount <= 3) return false;
                var text = children->NodeList[3];
                if (text == null || text->Type != NodeType.Text) return false;
                if (text->IsVisible() &&
                    !string.IsNullOrWhiteSpace(((AtkTextNode*)text)->NodeText.ToString())) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Exact 15:03:08 non-actionable state-15 observation. Numeric [6]=29
    /// and three equal tile icons resemble a tile-group payload, but do not
    /// establish which in-game operation (if any) is available. We require
    /// that node 104's choice list has NO active or labeled rows before
    /// allowing a short wait. Never perform a gameplay action here.
    /// </summary>
    private static bool IsObservedNumericTripleState15Frame(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady || !addon->IsVisible ||
                addon->AtkValues == null || addon->AtkValuesCount < 16) return false;
            var v = addon->AtkValues;
            if (v[0].Type != AtkValueType.Int || v[0].Int != 15 ||
                v[2].Type != AtkValueType.Int || v[2].Int != 3 ||
                v[3].Type != AtkValueType.Int || v[3].Int < 76041 || v[3].Int > 76077 ||
                v[5].Type != AtkValueType.Int || v[5].Int != 1 ||
                v[6].Type != AtkValueType.Int || v[6].Int != 29 ||
                v[7].Type != AtkValueType.Int || v[7].Int != 3 ||
                v[15].Type != AtkValueType.Int || v[15].Int != -1) return false;
            int icon = v[8].Int;
            if (icon < 76041 || icon > 76077) return false;
            for (int i = 8; i <= 10; i++)
                if (v[i].Type != AtkValueType.Int || v[i].Int != icon) return false;

            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null) return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            if (listNode == null || listNode->NodeId != 3 ||
                (int)listNode->Type != 1030) return false;
            if (!listNode->IsVisible()) return true;
            var list = ((AtkComponentNode*)listNode)->Component;
            if (list == null) return false;
            var mgr = &list->UldManager;
            if (mgr->NodeList == null) return false;
            for (int i = 0; i < Math.Min(7, (int)mgr->NodeListCount); i++)
            {
                var row = mgr->NodeList[i];
                if (row == null || (int)row->Type != 1029 || !row->IsVisible()) continue;
                // Any active response MUST take the unknown-prompt pause path.
                if (row->NodeFlags.HasFlag(NodeFlags.Enabled) &&
                    row->NodeFlags.HasFlag(NodeFlags.RespondToMouse)) return false;
                var component = ((AtkComponentNode*)row)->Component;
                if (component == null) return false;
                var children = &component->UldManager;
                if (children->NodeList == null || children->NodeListCount <= 3) return false;
                var text = children->NodeList[3];
                if (text == null || text->Type != NodeType.Text) return false;
                if (text->IsVisible() &&
                    !string.IsNullOrWhiteSpace(((AtkTextNode*)text)->NodeText.ToString()))
                    return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Read-only signature from both 15:09:35 and 15:16:32 snapshots,
    /// not a general state-15 Riichi reader. Both had state 15, a Discard
    /// hint, zero-valued numeric option slots, and visibly enabled Riichi/Pass
    /// rows. The only relevant observed difference was AtkValue[15]: 1 vs 0.
    /// These labels may be stale after Riichi, so this is NEVER an action
    /// authorization. It permits a bounded no-input wait only if this
    /// controller already issued Riichi during the current round.
    /// </summary>
    private static bool IsObservedState15RiichiPassNumericDiscard(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady || !addon->IsVisible ||
                addon->AtkValues == null || addon->AtkValuesCount < 16) return false;
            var v = addon->AtkValues;
            if (v[0].Type != AtkValueType.Int || v[0].Int != 15 ||
                v[2].Type != AtkValueType.Int || v[2].Int != 1 ||
                v[3].Type != AtkValueType.Int || v[3].Int < 76041 || v[3].Int > 76077 ||
                v[5].Type != AtkValueType.Int || v[5].Int != 1 ||
                !v[6].Type.ToString().Contains("String", StringComparison.Ordinal) ||
                !string.Equals(v[6].GetValueAsString().Trim(), "Discard", StringComparison.OrdinalIgnoreCase) ||
                // Both [15]=1 (15:09:35) and [15]=0 (15:16:32) were
                // observed with the same live Riichi / Pass controls. Reject
                // all other values: this is a narrow passive classification.
                v[15].Type != AtkValueType.Int ||
                (v[15].Int != 0 && v[15].Int != 1)) return false;
            for (int i = 7; i <= 14; ++i)
                if (v[i].Type != AtkValueType.Int || v[i].Int != 0) return false;
            return IsVisibleRiichiPassChoices(addon);
        }
        catch { return false; }
    }

    /// <summary>
    /// Read the *live, visible* option list, independently of numeric/string
    /// AtkValues. Used only to resolve option indices and detect conflicts:
    /// the recommended hint remains the primary action selector.
    /// Text nodes left under a HIDDEN list are deliberately excluded.
    /// Nothing here sends events or grants permission to act on a stale UI.
    /// </summary>
    public static IReadOnlyList<CallOption> ReadVisibleChoiceOptions(IGameGui gui, string addonName)
    {
        var result = new List<CallOption>();
        if (addonName is not ("Emj" or "EmjL")) return result;
        try
        {
            var addon = gui.GetAddonByName<AtkUnitBase>(addonName);
            if (addon == null || !addon->IsReady || !addon->IsVisible) return result;
            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null) return result;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return result;
            var node = outer->NodeList[0];
            if (node == null || node->NodeId != 3 || (int)node->Type != 1030 ||
                !node->IsVisible()) return result;
            var list = ((AtkComponentNode*)node)->Component;
            if (list == null) return result;
            var mgr = &list->UldManager;
            if (mgr->NodeList == null) return result;
            for (int i = 0; i < Math.Min(7, (int)mgr->NodeListCount); ++i)
            {
                var row = mgr->NodeList[i];
                uint rowId = i == 0 ? 2u : (uint)(21000 + i);
                if (row == null || row->NodeId != rowId || (int)row->Type != 1029 ||
                    !row->IsVisible() || !row->NodeFlags.HasFlag(NodeFlags.Enabled) ||
                    !row->NodeFlags.HasFlag(NodeFlags.RespondToMouse)) continue;
                var comp = ((AtkComponentNode*)row)->Component;
                if (comp == null) continue;
                var rowMgr = &comp->UldManager;
                if (rowMgr->NodeList == null || rowMgr->NodeListCount <= 3) continue;
                var labelNode = rowMgr->NodeList[3];
                if (labelNode == null || labelNode->Type != NodeType.Text ||
                    !labelNode->IsVisible()) continue;
                string label = ((AtkTextNode*)labelNode)->NodeText.ToString().Trim();
                if (label is "Ron" or "Tsumo" or "Riichi" or "Pon" or "Chi" or "Kan" or "Pass")
                    result.Add(new CallOption(i, label));
            }
        }
        catch { /* malformed/transient UI must not yield guessed options */ }
        return result;
    }

    /// <summary>Read an exact named response from the visible hint card only.</summary>
    private static string ReadVisibleHintCardAction(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || !((AtkResNode*)hintNode)->IsVisible() ||
                hintNode->Component == null) return "";
            var mgr = &hintNode->Component->UldManager;
            if (mgr->NodeList == null || mgr->NodeListCount <= 3) return "";
            var label = mgr->NodeList[3];
            if (label == null || label->Type != NodeType.Text || !label->IsVisible()) return "";
            string text = ((AtkTextNode*)label)->NodeText.ToString().Trim();
            return text is "Ron!" or "Pon!" or "Chi!" or "Kan!" or "Pass" ? text : "";
        }
        catch { return ""; }
    }

    private static bool IsVisiblePonPassChoices(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady) return false;
            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null) return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            if (listNode == null || listNode->NodeId != 3 ||
                (int)listNode->Type != 1030 || !listNode->IsVisible()) return false;
            var list = ((AtkComponentNode*)listNode)->Component;
            if (list == null) return false;
            var manager = &list->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 2) return false;
            return HasVisibleChoice(manager->NodeList[0], 2, "Pon") &&
                   HasVisibleChoice(manager->NodeList[1], 21001, "Pass");
        }
        catch { return false; }
    }

    private static bool IsVisibleRiichiPassChoices(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null || !addon->IsReady) return false;
            var group = addon->GetComponentNodeById(104);
            if (group == null || (int)group->AtkResNode.Type != 1052 ||
                !((AtkResNode*)group)->IsVisible() || group->Component == null)
                return false;
            var outer = &group->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return false;
            var listNode = outer->NodeList[0];
            if (listNode == null || listNode->NodeId != 3 || (int)listNode->Type != 1030 ||
                !listNode->IsVisible()) return false;
            var list = ((AtkComponentNode*)listNode)->Component;
            if (list == null) return false;
            var manager = &list->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 2) return false;
            return HasVisibleChoice(manager->NodeList[0], 2, "Riichi") &&
                   HasVisibleChoice(manager->NodeList[1], 21001, "Pass");
        }
        catch { return false; }
    }

    private static bool HasVisibleChoice(AtkResNode* node, uint id, string expected)
    {
        if (node == null || node->NodeId != id || (int)node->Type != 1029 ||
            !node->IsVisible() ||
            !node->NodeFlags.HasFlag(NodeFlags.Enabled) ||
            !node->NodeFlags.HasFlag(NodeFlags.RespondToMouse)) return false;
        var component = ((AtkComponentNode*)node)->Component;
        if (component == null) return false;
        var manager = &component->UldManager;
        if (manager->NodeList == null || manager->NodeListCount <= 3) return false;
        var text = manager->NodeList[3];
        return text != null && text->Type == NodeType.Text && text->IsVisible() &&
               string.Equals(((AtkTextNode*)text)->NodeText.ToString().Trim(), expected,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recognize an explicitly named, visible Kan recommendation on the
    /// self-action hint panel. In particular, numeric AtkValue[6] = 4 is
    /// NOT considered Kan by itself. Do not override a visible Discard hint
    /// or rely on old call-announcement text elsewhere in the addon.
    /// </summary>
    internal static bool IsVisibleExclusiveKanHint(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || !((AtkResNode*)hintNode)->IsVisible() ||
                hintNode->Component == null) return false;
            var mgr = &hintNode->Component->UldManager;
            if (mgr->NodeList == null || mgr->NodeListCount <= 3) return false;
            var actionNode = mgr->NodeList[3];
            if (actionNode == null || actionNode->Type != NodeType.Text ||
                !actionNode->IsVisible()) return false;
            string visibleText = ((AtkTextNode*)actionNode)->NodeText.ToString().Trim();
            if (!string.Equals(visibleText, "Kan!", StringComparison.OrdinalIgnoreCase))
                return false;
            var discardNode = mgr->NodeList[2];
            if (discardNode != null && discardNode->Type == NodeType.Text &&
                discardNode->IsVisible() &&
                string.Equals(((AtkTextNode*)discardNode)->NodeText.ToString().Trim(),
                    "Discard", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Exact self-draw win hint layout from the captured Tsumo/Pass screen:
    /// Emj state 6, hint component node 45, visible text child003 "Tsumo!",
    /// and the ordinary discard-label child002 hidden. Does not assume
    /// numeric AtkValues[6] is an option label or a tile id.
    /// </summary>
    private static bool IsVisibleTsumoHint(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || !((AtkResNode*)hintNode)->IsVisible() ||
                hintNode->Component == null) return false;
            var manager = &hintNode->Component->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 4) return false;
            var discardText = manager->NodeList[2];
            var actionText = manager->NodeList[3];
            if (actionText == null || actionText->Type != NodeType.Text ||
                !actionText->IsVisible() ||
                (discardText != null && discardText->IsVisible())) return false;
            string label = ((AtkTextNode*)actionText)->NodeText.ToString().Trim();
            return string.Equals(label, "Tsumo!", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// The combined Riichi!/Discard card identified in the user's UI capture.
    /// This is only an extra check of the *hint*, not authorization by itself:
    /// the caller must also observe state 30 and exactly Riichi/Pass buttons.
    /// </summary>
    private static bool IsVisibleRiichiDiscardHint(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || !((AtkResNode*)hintNode)->IsVisible() ||
                hintNode->Component == null) return false;
            var manager = &hintNode->Component->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 5) return false;
            var discard = manager->NodeList[2];
            var riichi = manager->NodeList[3];
            var tile = manager->NodeList[4];
            if (discard == null || discard->Type != NodeType.Text || !discard->IsVisible() ||
                riichi == null || riichi->Type != NodeType.Text || !riichi->IsVisible() ||
                tile == null || !tile->IsVisible()) return false;
            var discardText = ((AtkTextNode*)discard)->NodeText.ToString().Trim();
            var riichiText = ((AtkTextNode*)riichi)->NodeText.ToString().Trim().TrimEnd('!', '！');
            return string.Equals(discardText, "Discard", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(riichiText, "Riichi", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
