using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Read-only inspection of an intermittent state-6 Riichi / Pass mismatch.
/// Never infer an actionable Riichi offer from the hint card alone: the same
/// card is used for the discard immediately after Riichi was declared.
/// </summary>
internal static unsafe class RiichiPromptDiagnostics
{
    public static string Inspect(IGameGui gameGui)
    {
        var lines = new List<string>
        {
            "# Doman Hint Pilot v0.8.6 — focused Riichi / opponent call prompt diagnostic",
            "# Read-only: no callbacks, mouse events, button events, or gameplay actions",
            "# Capture active Riichi/Pass or opponent call prompts; no actions are sent.",
            "# A state-6 Riichi!/Discard hint can also mean Riichi was ALREADY accepted.",
        };
        var prompt = CallPromptReader.Read(gameGui);
        var hint = AddonSnapshot.ReadCurrentHint(gameGui);
        lines.Add($"Prompt: addon={prompt.Addon}, state={prompt.State}, hintVisible={prompt.HintVisible}, action='{Clean(prompt.RecommendedAction)}', options='{Clean(prompt.Summary)}', riichiOffer={prompt.HasRiichiPassOffer}");
        lines.Add($"LiveHint: state={hint.State}, action='{Clean(hint.Action)}', tile={hint.IconId}, source='{Clean(hint.Source)}'");
        var foundAddon = false;
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null || !addon->IsVisible || !addon->IsReady) continue;
            foundAddon = true;
            lines.Add($"ADDON {name}: ready=true, nodeCount={addon->UldManager.NodeListCount}, AtkValuesCount={addon->AtkValuesCount}");
            if (addon->AtkValues != null)
            {
                // Preserve the actual type. During the observed state-6 failure,
                // these were numeric, so a string-only parser was guaranteed to miss them.
                for (int i = 0; i < Math.Min(40, (int)addon->AtkValuesCount); i++)
                {
                    var v = &addon->AtkValues[i];
                    string value;
                    try { value = Clean(v->GetValueAsString()); }
                    catch { value = "(unreadable)"; }
                    lines.Add($"VALUE/{i:000} type={v->Type} value='{value}' int={v->Int}");
                }
            }
            // The earlier snapshot showed no clicks under Root/228, but
            // absence of events alone does not identify whether the panel
            // was hidden or had inactive rows. Record the component state.
            var responsePanel = addon->GetComponentNodeById(104);
            lines.Add(responsePanel == null
                ? "CHOICE PANEL node104: missing"
                : $"CHOICE PANEL node104: type={(int)responsePanel->AtkResNode.Type} visible={((AtkResNode*)responsePanel)->IsVisible()} " +
                  $"componentPresent={responsePanel->Component != null} emptyState15={prompt.IsVerifiedEmptyState15} blankState15={prompt.IsVerifiedBlankState15Panel} numericTripleState15={prompt.IsVerifiedNumericTripleState15Frame} unresolvedState15RiichiPass={prompt.HasUnresolvedState15RiichiPassUi} hiddenState15List={prompt.IsVerifiedHiddenState15ChoiceList}");
            // Report exact row visibility, flags and text, not only registered
            // click events. Numeric states may show a panel shell with hidden
            // or inactive options; the distinction matters for safe waiting.
            InspectChoiceRows(responsePanel, lines);
            var list = addon->UldManager.NodeList;
            if (list == null)
            {
                lines.Add("ERROR: root node list unavailable");
                continue;
            }
            int budget = 8000, matches = 0, clicks = 0;
            int count = Math.Min(600, (int)addon->UldManager.NodeListCount);
            for (int i = 0; i < count && budget > 0; i++)
                Walk(list[i], $"Root/{i:000}", "", 5, lines, ref budget, ref matches, ref clicks);
            lines.Add($"SUMMARY {name}: scannedRoots={count} matchingVisibleTexts={matches} registeredButtonClicks={clicks} budgetRemaining={budget}");
        }
        if (!foundAddon) lines.Add("ERROR: no visible and ready Emj / EmjL addon");
        lines.Add("# Event/type output is for diagnosis only. No callback number is authorized by this capture.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void InspectChoiceRows(AtkComponentNode* panel, List<string> lines)
    {
        try
        {
            if (panel == null || panel->Component == null) return;
            var outer = &panel->Component->UldManager;
            if (outer->NodeList == null || outer->NodeListCount < 1) return;
            var first = outer->NodeList[0];
            if (first == null || (int)first->Type != 1030) return;
            lines.Add($"CHOICE LIST: visible={first->IsVisible()} flags={first->NodeFlags}");
            var comp = ((AtkComponentNode*)first)->Component;
            if (comp == null) return;
            var mgr = &comp->UldManager;
            if (mgr->NodeList == null) return;
            for (int i = 0; i < Math.Min(7, (int)mgr->NodeListCount); i++)
            {
                var row = mgr->NodeList[i];
                if (row == null) continue;
                string label = "";
                if ((int)row->Type >= 1000)
                {
                    var c = ((AtkComponentNode*)row)->Component;
                    if (c != null)
                    {
                        var inner = &c->UldManager;
                        if (inner->NodeList != null && inner->NodeListCount > 3)
                        {
                            var text = inner->NodeList[3];
                            if (text != null && text->Type == NodeType.Text)
                                label = ((AtkTextNode*)text)->NodeText.ToString();
                        }
                    }
                }
                lines.Add($"CHOICE ROW/{i:000}: id={row->NodeId} type={(int)row->Type} visible={row->IsVisible()} flags={row->NodeFlags} text='{Clean(label)}'");
            }
        }
        catch (Exception ex) { lines.Add("CHOICE ROW scan unavailable: " + ex.GetType().Name); }
    }

    private static void Walk(AtkResNode* node, string path, string ancestorSummary,
        int depth, List<string> lines, ref int budget, ref int matches, ref int clicks)
    {
        if (node == null || budget-- <= 0 || !node->IsVisible()) return;
        string current = $"{path} id={node->NodeId} type={(int)node->Type} flags={node->NodeFlags} " +
            $"pos=({node->X:F0},{node->Y:F0}) size=({node->Width},{node->Height})";
        if (node->Type == NodeType.Text)
        {
            try
            {
                string label = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (IsRelevant(label))
                {
                    matches++;
                    lines.Add($"MATCH text='{Clean(label)}' {current}");
                    if (ancestorSummary.Length > 0) lines.Add("  ANCESTORS: " + ancestorSummary);
                }
            }
            catch { /* ignore unreadable text */ }
        }
        // The actual Next button in Emj was node type 1007, not 1001.
        // Inspect registered events on all visible node types; do not cast as Button.
        var ev = node->AtkEventManager.Event;
        for (int i = 0; i < 24 && ev != null; i++, ev = ev->NextEvent)
        {
            if (ev->State.EventType != AtkEventType.ButtonClick) continue;
            clicks++;
            if (clicks <= 70)
                lines.Add($"CLICK {current} eventType={ev->State.EventType} param={ev->Param}");
        }
        if (depth <= 0 || (int)node->Type < 1000) return;
        var component = ((AtkComponentNode*)node)->Component;
        if (component == null) return;
        var manager = &component->UldManager;
        if (manager->NodeList == null) return;
        // Retain up to two ancestor component paths for matching text labels.
        string nextAncestors = ancestorSummary.Length == 0
            ? current
            : ancestorSummary + " -> " + current;
        if (nextAncestors.Length > 420) nextAncestors = nextAncestors.Substring(nextAncestors.Length - 420);
        for (int i = 0; i < Math.Min(100, (int)manager->NodeListCount) && budget > 0; i++)
            Walk(manager->NodeList[i], path + $"/Child/{i:000}", nextAncestors,
                depth - 1, lines, ref budget, ref matches, ref clicks);
    }

    private static bool IsRelevant(string text) =>
        text.Contains("Riichi", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Pass", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Discard", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Tsumo", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Kan", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Chi", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Pon", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Ron", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return text.Length > 220 ? text.Substring(0, 220) + "..." : text;
    }
}
