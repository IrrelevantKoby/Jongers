using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Read-only UI discovery for results screens shown after Emj/EmjL close.
/// No callbacks are sent, no UI is modified. Run only on user demand.
/// </summary>
internal static unsafe class ResultAddonDiagnostics
{
    public static string ListVisibleAddons()
    {
        var lines = new List<string>
        {
            "# Doman Hint Pilot v0.7.4 — visible-addon discovery",
            "# Read-only: no button presses, callbacks, or game actions",
            "# Capture WHILE the final Match Results / End Match window is visible.",
            "# The name beside 'Match Results' is unknown until confirmed by this capture.",
        };

        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null)
        {
            lines.Add("ERROR: Game UI stage or addon manager is unavailable.");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        var manager = &stage->RaptureAtkUnitManager->AtkUnitManager;
        var units = &manager->AllLoadedUnitsList;
        var entries = units->Entries;
        int max = Math.Min((int)units->Count, entries.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int visibleCount = 0;
        for (int i = 0; i < max; i++)
        {
            var addon = entries[i].Value;
            if (addon == null || !addon->IsVisible || !addon->IsReady) continue;
            var name = addon->NameString;
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            visibleCount++;
            lines.Add($"Visible/{visibleCount:000} Name={name} AtkValues={addon->AtkValuesCount} " +
                $"Nodes={addon->UldManager.NodeListCount} " +
                $"(run /mahint addon {name} for a focused read-only capture)");
            var preview = PreviewText(addon);
            if (preview.Length > 0) lines.Add("  TextPreview: " + preview);
        }

        lines.Insert(4, $"# Loaded list entries examined: {max}; visible unique addon names: {visibleCount}");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string InspectNamedAddon(IGameGui gameGui, string addonName)
    {
        var lines = new List<string>
        {
            "# Doman Hint Pilot v0.7.4 — focused addon diagnostic",
            "# Read-only: no callbacks, clicks, or gameplay actions",
            "Addon = " + addonName,
        };
        if (string.IsNullOrWhiteSpace(addonName) || addonName.Length > 80 ||
            addonName.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            lines.Add("ERROR: Supply a single addon name from /mahint addons (letters, digits, '-' or '_').");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
        {
            lines.Add("ERROR: Named addon not currently visible/ready. Capture while results are open.");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        lines.Add($"AtkValuesCount = {addon->AtkValuesCount}");
        lines.Add($"RootNodeListCount = {addon->UldManager.NodeListCount}");
        if (addon->AtkValues != null)
        {
            for (int i = 0; i < Math.Min((int)addon->AtkValuesCount, 256); i++)
            {
                var value = &addon->AtkValues[i];
                string rendered;
                try { rendered = value->GetValueAsString(); }
                catch { rendered = "(unreadable)"; }
                lines.Add($"AtkValue/{i:000} = {value->Type}: {Clean(rendered, 400)}");
            }
        }

        int remainingNodes = 400;
        var nodeList = addon->UldManager.NodeList;
        if (nodeList != null)
            for (int i = 0; i < Math.Min((int)addon->UldManager.NodeListCount, 128); i++)
                Walk(lines, $"Root/{i:000}", nodeList[i], 3, ref remainingNodes);
        if (remainingNodes <= 0) lines.Add("# Node capture truncated at 400 entries.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string PreviewText(AtkUnitBase* addon)
    {
        var found = new List<string>();
        int budget = 90;
        var nodeList = addon->UldManager.NodeList;
        if (nodeList != null)
        {
            for (int i = 0; i < Math.Min((int)addon->UldManager.NodeListCount, 70) && budget > 0; i++)
                PreviewNode(found, nodeList[i], 2, ref budget);
        }
        return string.Join(" | ", found.Distinct(StringComparer.Ordinal).Take(8));
    }

    private static void PreviewNode(List<string> result, AtkResNode* node, int depth, ref int budget)
    {
        if (node == null || budget-- <= 0 || !node->IsVisible()) return;
        if (node->Type == NodeType.Text)
        {
            try
            {
                var text = Clean(((AtkTextNode*)node)->NodeText.ToString(), 75);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            catch { /* diagnostic best effort */ }
        }
        if (depth <= 0 || (int)node->Type < 1000) return;
        var cnode = (AtkComponentNode*)node;
        if (cnode->Component == null) return;
        var list = &cnode->Component->UldManager;
        if (list->NodeList == null) return;
        for (int i = 0; i < Math.Min((int)list->NodeListCount, 40) && budget > 0; i++)
            PreviewNode(result, list->NodeList[i], depth - 1, ref budget);
    }

    private static void Walk(List<string> lines, string path, AtkResNode* node, int depth, ref int budget)
    {
        if (node == null || budget-- <= 0) return;
        var detail = $"NodeId={node->NodeId} Type={node->Type} Visible={node->IsVisible()} " +
            $"Position=({node->X:F0},{node->Y:F0}) Size=({node->Width},{node->Height})";
        if (node->Type == NodeType.Text)
        {
            try { detail += " Text=\"" + Clean(((AtkTextNode*)node)->NodeText.ToString(), 200) + "\""; }
            catch { detail += " Text=(unreadable)"; }
        }
        lines.Add(path + " = " + detail);
        if (depth <= 0 || (int)node->Type < 1000) return;
        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null) return;
        var manager = &componentNode->Component->UldManager;
        if (manager->NodeList == null) return;
        for (int i = 0; i < Math.Min((int)manager->NodeListCount, 64) && budget > 0; i++)
            Walk(lines, path + $"/Child/{i:000}", manager->NodeList[i], depth - 1, ref budget);
    }

    private static string Clean(string text, int maxLen)
    {
        var clean = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return clean.Length > maxLen ? clean.Substring(0, maxLen) + "..." : clean;
    }
}
