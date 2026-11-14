using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Read-only survey of button components and visible text in Emj / EmjL round results.
/// The old generic dump truncated after 400 entries before it reached the Next button.
/// This targeted pass walks ALL root nodes, not only the first 128, and emits only
/// meaningful button and text candidates. No input or gameplay action is sent.
/// </summary>
internal static unsafe class RoundResultsDiagnostics
{
    public static string Inspect(IGameGui gameGui)
    {
        var lines = new List<string>
        {
            "# Doman Hint Pilot v0.7.7 — round results Next component discovery",
            "# Read-only: no UI callbacks, button events, mouse events, or game actions",
            "# Capture while the round Draw / Ron / Tsumo results panel displays NEXT.",
        };
        AtkUnitBase* addon = null;
        string addonName = "(none)";
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var candidate = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (candidate == null || !candidate->IsVisible || !candidate->IsReady) continue;
            addon = candidate;
            addonName = name;
            break;
        }
        if (addon == null)
        {
            lines.Add("ERROR: No visible and ready Emj/EmjL addon.");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        lines.Add("Addon = " + addonName);
        lines.Add("RootNodeListCount = " + addon->UldManager.NodeListCount);
        int state = addon->AtkValues != null && addon->AtkValuesCount > 0
            ? addon->AtkValues[0].Int : -1;
        lines.Add("State = " + state);
        string resultLabel = "(unavailable)";
        if (addon->AtkValues != null && addon->AtkValuesCount > 5)
        {
            try { resultLabel = addon->AtkValues[5].GetValueAsString(); }
            catch { resultLabel = "(unreadable)"; }
        }
        lines.Add("AtkValue/005 = " + resultLabel);
        if (state != 29 && state != 32 && state != 2)
            lines.Add("NOTE: Not a previously observed result state. No actions are possible from this command.");

        var list = addon->UldManager.NodeList;
        int count = Math.Min((int)addon->UldManager.NodeListCount, 600);
        if (list == null)
        {
            lines.Add("ERROR: Root node list is null.");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }
        int buttons = 0;
        int textMatches = 0;
        int remaining = 6000;
        for (int i = 0; i < count && remaining > 0; i++)
            Walk(list[i], $"Root/{i:000}", 4, lines, ref remaining, ref buttons, ref textMatches);
        lines.Add($"Summary: scannedRoot={count}, ordinaryButtonComponents={buttons}, visibleResultTexts={textMatches}, walkBudgetRemaining={remaining}");
        lines.Add("# Focused inspection: locate the actual owner of the visible Next text, even when it is not an ordinary button.");
        int candidates = 0;
        for (int i = 0; i < count; i++)
        {
            var root = list[i];
            if (root == null || !root->IsVisible() || (int)root->Type < 1000) continue;
            var component = ((AtkComponentNode*)root)->Component;
            if (component == null || component->UldManager.NodeList == null) continue;
            var childNodes = component->UldManager.NodeList;
            int childCount = Math.Min((int)component->UldManager.NodeListCount, 96);
            bool hasNext = false;
            for (int j = 0; j < childCount; j++)
            {
                var child = childNodes[j];
                if (child == null || !child->IsVisible() || child->Type != NodeType.Text) continue;
                try { if (((AtkTextNode*)child)->NodeText.ToString().Trim() == "Next") hasNext = true; }
                catch { /* Not a readable text node */ }
            }
            if (!hasNext) continue;
            candidates++;
            lines.Add($"NEXT CANDIDATE Root/{i:000} id={root->NodeId} type={(int)root->Type} children={childCount}");
            TraceNextNode(root, $"Root/{i:000}", 3, lines, 100);
        }
        lines.Add($"NextCandidateCount={candidates}");
        if (candidates == 0) lines.Add("Next text has no visible component parent; do NOT guess an event or callback.");
        if (remaining == 0) lines.Add("WARNING: Node scan reached safety budget; notify developer.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void Walk(AtkResNode* node, string path, int depth, List<string> lines,
        ref int remaining, ref int buttons, ref int textMatches)
    {
        if (node == null || remaining-- <= 0) return;
        if (!node->IsVisible()) return;
        if (node->Type == NodeType.Text)
        {
            string value;
            try { value = ((AtkTextNode*)node)->NodeText.ToString(); }
            catch { value = "(unreadable)"; }
            if (value.Contains("Next", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Draw", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Ron", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Tsumo", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Result", StringComparison.OrdinalIgnoreCase))
            {
                textMatches++;
                lines.Add($"TEXT {path} id={node->NodeId} text=\"{Clean(value)}\" pos=({node->X:F0},{node->Y:F0})");
            }
        }
        if ((int)node->Type < 1000 || depth <= 0) return;
        var componentNode = (AtkComponentNode*)node;
        var component = componentNode->Component;
        if (component == null) return;
        var manager = &component->UldManager;
        if ((int)node->Type == 1001)
        {
            buttons++;
            bool enabled = ((AtkComponentButton*)component)->IsEnabled;
            var labels = new List<string>();
            if (manager->NodeList != null)
            {
                for (int j = 0; j < Math.Min((int)manager->NodeListCount, 64); j++)
                {
                    var child = manager->NodeList[j];
                    if (child == null || child->Type != NodeType.Text) continue;
                    try { labels.Add(Clean(((AtkTextNode*)child)->NodeText.ToString())); }
                    catch { labels.Add("(unreadable)"); }
                }
            }
            bool registeredClick = false;
            var evt = node->AtkEventManager.Event;
            for (int j = 0; j < 30 && evt != null; j++, evt = evt->NextEvent)
            {
                if (evt->State.EventType == AtkEventType.ButtonClick)
                {
                    registeredClick = true;
                    break;
                }
            }
            lines.Add($"BUTTON {path} id={node->NodeId} type={node->Type} enabled={enabled} " +
                $"clickRegistered={registeredClick} pos=({node->X:F0},{node->Y:F0}) " +
                $"size=({node->Width},{node->Height}) labels=[{string.Join(" | ", labels)}]");
        }
        if (manager->NodeList == null) return;
        for (int j = 0; j < Math.Min((int)manager->NodeListCount, 96) && remaining > 0; j++)
            Walk(manager->NodeList[j], path + $"/Child/{j:000}", depth - 1,
                lines, ref remaining, ref buttons, ref textMatches);
    }

    // Read-only inspection of the specific component that owns the displayed Next label.
    // Do not assume it is an AtkComponentButton: observed client uses component node type 1007.
    private static void TraceNextNode(AtkResNode* node, string path, int depth,
        List<string> lines, int budget)
    {
        if (node == null || budget <= 0) return;
        string label = "";
        if (node->Type == NodeType.Text)
        {
            try { label = " text=\"" + Clean(((AtkTextNode*)node)->NodeText.ToString()) + "\""; }
            catch { label = " text=(unreadable)"; }
        }
        lines.Add($"NODE {path} id={node->NodeId} type={(int)node->Type} visible={node->IsVisible()} " +
                  $"flags={node->NodeFlags} pos=({node->X:F0},{node->Y:F0}) " +
                  $"size=({node->Width},{node->Height}){label}");
        var e = node->AtkEventManager.Event;
        int eventCount = 0;
        for (; e != null && eventCount < 30; e = e->NextEvent, eventCount++)
            lines.Add($"EVENT {path} type={e->State.EventType} param={e->Param}");
        if (eventCount == 0) lines.Add($"EVENT {path} (none registered on this node)");
        if (depth <= 0 || (int)node->Type < 1000) return;
        var comp = ((AtkComponentNode*)node)->Component;
        if (comp == null || comp->UldManager.NodeList == null) return;
        var nodes = comp->UldManager.NodeList;
        var total = Math.Min((int)comp->UldManager.NodeListCount, 40);
        for (int i = 0; i < total; i++)
            TraceNextNode(nodes[i], path + $"/Child/{i:000}", depth - 1, lines, budget - i - 1);
    }

    private static string Clean(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return value.Length <= 160 ? value : value[..160] + "...";
    }
}
