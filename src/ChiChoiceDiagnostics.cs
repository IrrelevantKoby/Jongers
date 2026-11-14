using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Passive inspection of the SECOND Chi step, in which multiple legal meld
/// sequences may be shown. This module NEVER selects a sequence or sends an
/// input. Capture before and after the initial one-shot Chi response.
/// </summary>
internal static unsafe class ChiChoiceDiagnostics
{
    private const int MaxLines = 400;

    public static string Inspect(IGameGui gameGui)
    {
        var lines = new List<string>
        {
            "# Doman Hint Pilot v0.7.18 — Chi meld choice diagnostic",
            "# READ ONLY. No UI callbacks, mouse events, button events, or gameplay inputs.",
            "# Run /mahint chi before selecting Chi AND while multiple Chi combinations are displayed.",
            "# Choice indices and event parameters here are observations, not authorized action commands."
        };
        var prompt = CallPromptReader.Read(gameGui);
        var hint = AddonSnapshot.ReadCurrentHint(gameGui);
        lines.Add($"PROMPT addon={prompt.Addon} state={prompt.State} hintVisible={prompt.HintVisible} action='{Clean(prompt.RecommendedAction)}' options='{Clean(prompt.Summary)}'");
        lines.Add($"HINT addon={hint.Addon} state={hint.State} action='{Clean(hint.Action)}' icon={hint.IconId} source='{Clean(hint.Source)}'");
        bool found = false;
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null || !addon->IsVisible || !addon->IsReady) continue;
            found = true;
            lines.Add($"ADDON {name} ready=true nodes={addon->UldManager.NodeListCount} atkCount={addon->AtkValuesCount}");
            if (addon->AtkValues != null)
            {
                for (int i = 0; i < Math.Min(85, (int)addon->AtkValuesCount); ++i)
                {
                    var val = &addon->AtkValues[i];
                    string value;
                    try { value = Clean(val->GetValueAsString()); }
                    catch { value = "(unreadable)"; }
                    lines.Add($"VALUE/{i:000} type={val->Type} text='{value}'");
                }
            }
            var list = addon->UldManager.NodeList;
            if (list == null) { lines.Add("ROOT: unavailable"); continue; }
            int roots = Math.Min(600, (int)addon->UldManager.NodeListCount);
            int budget = 14000, matching = 0, clickCount = 0;
            // Global pass: find all visible words and ButtonClick owners (including
            // non-button component types, as with the results Next button).
            for (int i = 0; i < roots && budget > 0; ++i)
                Scan(list[i], $"Root/{i:000}", 6, false, false, lines,
                    ref budget, ref matching, ref clickCount);
            lines.Add($"SUMMARY global roots={roots} matchedTexts={matching} clicks={clickCount} remaining={budget}");
            // Focused expansion now includes Root/280, which the user's live
            // 13:17:45 capture confirmed contains Chi LEFT/RIGHT and Cancel.
            foreach (int idx in new[] { 228, 280, 286 })
            {
                if (idx >= roots) continue;
                lines.Add($"# FOCUSED TREE Root/{idx:000} (node IDs / labels / images / click owners)");
                int localBudget = 2000, localMatching = 0, localClicks = 0;
                Scan(list[idx], $"Root/{idx:000}", 7, true, true, lines,
                    ref localBudget, ref localMatching, ref localClicks);
            }
        }
        if (!found) lines.Add("ERROR: no visible ready Emj/EmjL UI");
        lines.Add("# END. This capture does not select or authorize any Chi meld option.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void Scan(AtkResNode* node, string path, int depth,
        bool verbose, bool images, List<string> lines,
        ref int budget, ref int matching, ref int clickCount)
    {
        if (node == null || budget-- <= 0 || !node->IsVisible()) return;
        if (lines.Count >= MaxLines && !verbose) return;
        string details = $"{path} id={node->NodeId} type={(int)node->Type} flags={node->NodeFlags} " +
            $"xy=({node->X:F0},{node->Y:F0}) wh=({node->Width},{node->Height})";
        if (verbose && lines.Count < MaxLines + 230)
            lines.Add("NODE " + details);
        if (node->Type == NodeType.Text)
        {
            try
            {
                string label = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (label.Length > 0 && (verbose || Relevant(label)))
                {
                    matching++;
                    if (lines.Count < MaxLines + 230)
                        lines.Add($"TEXT {details} text='{Clean(label)}'");
                }
            }
            catch { }
        }
        if (images && node->Type == NodeType.Image)
        {
            try
            {
                if (lines.Count < MaxLines + 230)
                    lines.Add($"IMAGE {details} {Clean(HandTileScanner.DescribeImage((AtkImageNode*)node))}");
            }
            catch { }
        }
        // Never treat button event parameters as a complete Chi choice model.
        var evt = node->AtkEventManager.Event;
        for (int j = 0; j < 18 && evt != null; ++j, evt = evt->NextEvent)
        {
            if (evt->State.EventType != AtkEventType.ButtonClick) continue;
            clickCount++;
            if (lines.Count < MaxLines + 230 && (verbose || clickCount <= 75))
                lines.Add($"CLICK {details} eventType={evt->State.EventType} param={evt->Param}");
        }
        if (depth <= 0 || (int)node->Type < 1000) return;
        var component = ((AtkComponentNode*)node)->Component;
        if (component == null) return;
        var manager = &component->UldManager;
        if (manager->NodeList == null) return;
        for (int i = 0; i < Math.Min(100, (int)manager->NodeListCount) && budget > 0; ++i)
            Scan(manager->NodeList[i], path + $"/Child/{i:000}", depth - 1,
                verbose, images, lines, ref budget, ref matching, ref clickCount);
    }

    private static bool Relevant(string s) =>
        s.Contains("Chi", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Pass", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Cancel", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Discard", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Choose", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string s)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length > 240 ? s.Substring(0, 240) + "..." : s;
    }
}
