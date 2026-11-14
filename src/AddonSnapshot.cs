using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Passive UI snapshot only. No FireCallback, key injection, memory patching, or queue operation.
/// The UI node layout can change with patches; do not turn these observed values into a bot
/// without first confirming observed values in live NPC/unranked tests.
/// </summary>
public sealed unsafe class AddonSnapshot
{
    public string AddonName { get; }
    public IReadOnlyDictionary<string, string> Entries { get; }

    public bool IsPresent => AddonName != "(none)";

    private AddonSnapshot(string addonName, Dictionary<string, string> entries)
    {
        AddonName = addonName;
        Entries = entries;
    }

    // Lightweight polling method: do NOT run the full node snapshot every frame.
    public static (string Addon, int State) Peek(IGameGui gameGui)
    {
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null || !addon->IsVisible) continue;
            if (addon->AtkValues == null || addon->AtkValuesCount == 0)
                return (name, -1);
            return (name, addon->AtkValues[0].Int);
        }
        return ("(none)", -1);
    }

    /// <summary>
    /// Passive reading of a discard recommendation. Normally AtkValues[2]
    /// holds an icon ID. During optional self actions (Kan/Pass), however,
    /// [2] can be a small integer instead. The visible hint tile texture is
    /// the fallback; it must be a readable Mahjong icon on a visible Discard
    /// label. State 6 with the exact two-label Riichi!/Discard layout uses
    /// child 4 of the hint panel instead, as verified in a later capture.
    /// </summary>
    public static HintReading ReadCurrentHint(IGameGui gameGui)
    {
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon == null || !addon->IsVisible) continue;

            int state = -1;
            int iconId = -1;
            string action = "";
            bool validValueArray = addon->AtkValues != null && addon->AtkValuesCount >= 7;
            if (validValueArray)
            {
                state = addon->AtkValues[0].Int;
                iconId = addon->AtkValues[2].Int;
                try { action = addon->AtkValues[6].GetValueAsString(); }
                catch { action = ""; }
            }

            var hintNode = addon->GetComponentNodeById(45);
            bool panelVisible = hintNode != null && ((AtkResNode*)hintNode)->IsVisible();
            bool isDiscard = panelVisible && string.Equals(action.Trim(), "Discard", StringComparison.OrdinalIgnoreCase)
                && IsMahjongIcon(iconId);
            string source = isDiscard ? "AtkValue/002" : "none";

            // In captures of state 30 with Kan/Pass visible, AtkValue[2]
            // carried the numeric value 4 while the single-card hint tile
            // displayed icon 76044. Require a visible 'Discard' label and
            // its companion visible tile before using that texture.
            if (panelVisible && (state == 6 || state == 30) && !isDiscard)
            {
                int visualId = ReadSingleDiscardHintIcon(hintNode);
                if (IsMahjongIcon(visualId))
                {
                    iconId = visualId;
                    isDiscard = true;
                    source = "HintPanel/child007/image";
                }
            }

            // After Riichi is declared, the panel retains two separate labels:
            // 'Riichi!' (child 3) and 'Discard' (child 2). The selected tile
            // is displayed in child 4, not child 7. In the observed state 6
            // capture, AtkValue[2] was 0 and AtkValue[6] was 5, i.e. not a
            // normal text/action + icon pair. Do not infer it from those values.
            if (panelVisible && state == 6 && !isDiscard)
            {
                int riichiDiscardId = ReadRiichiDiscardHintIcon(hintNode);
                if (IsMahjongIcon(riichiDiscardId))
                {
                    iconId = riichiDiscardId;
                    isDiscard = true;
                    action = "Riichi! / Discard";
                    source = "HintPanel/RiichiDiscard/child004/image";
                }
            }

            if ((state is 6 or 30) && panelVisible && !isDiscard &&
                CallPromptReader.IsVisibleExclusiveKanHint(hintNode))
                action = "Kan!";

            return new HintReading(name, state, panelVisible, action,
                iconId, IconToKnownTile(iconId), isDiscard, source);
        }

        return HintReading.None;
    }

    private static bool IsMahjongIcon(int id) => id >= 76041 && id <= 76077;

    /// <summary>
    /// The one-card Discard display confirmed in the Kan/Pass capture has
    /// text node child 3 and tile component child 7. The two-card Riichi
    /// display has its own strict read path.
    /// </summary>
    private static int ReadSingleDiscardHintIcon(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || hintNode->Component == null) return -1;
            var manager = &hintNode->Component->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 8) return -1;
            var labelNode = manager->NodeList[3];
            var tileNode = manager->NodeList[7];
            if (labelNode == null || labelNode->Type != NodeType.Text || !labelNode->IsVisible())
                return -1;
            var label = ((AtkTextNode*)labelNode)->NodeText.ToString().Trim();
            if (!string.Equals(label, "Discard", StringComparison.OrdinalIgnoreCase)) return -1;
            if (tileNode == null || (int)tileNode->Type < 1000 || !tileNode->IsVisible())
                return -1;
            var tileComponent = (AtkComponentNode*)tileNode;
            if (tileComponent->Component == null) return -1;
            var nested = &tileComponent->Component->UldManager;
            if (nested->NodeList == null || nested->NodeListCount < 4) return -1;
            var imageNode = nested->NodeList[3];
            if (imageNode == null || imageNode->Type != NodeType.Image || !imageNode->IsVisible())
                return -1;
            var (id, _) = HandTileScanner.ReadImageIcon((AtkImageNode*)imageNode);
            return id;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Strict read of the post-declaration Riichi discard panel at state 6.
    /// Based on the 2026-11-12 09:48:24 capture, which showed:
    /// child 2='Discard', child 3='Riichi!', visible child 4 icon=76045,
    /// and hidden child 7. Never use a hidden/stale texture as a recommendation.
    /// </summary>
    private static int ReadRiichiDiscardHintIcon(AtkComponentNode* hintNode)
    {
        try
        {
            if (hintNode == null || hintNode->Component == null) return -1;
            var manager = &hintNode->Component->UldManager;
            if (manager->NodeList == null || manager->NodeListCount < 8) return -1;

            var discardLabelNode = manager->NodeList[2];
            var riichiLabelNode = manager->NodeList[3];
            var tileNode = manager->NodeList[4];
            var singleTileNode = manager->NodeList[7];
            if (discardLabelNode == null || discardLabelNode->Type != NodeType.Text ||
                !discardLabelNode->IsVisible()) return -1;
            if (riichiLabelNode == null || riichiLabelNode->Type != NodeType.Text ||
                !riichiLabelNode->IsVisible()) return -1;

            string discardLabel = ((AtkTextNode*)discardLabelNode)->NodeText.ToString().Trim();
            string riichiLabel = ((AtkTextNode*)riichiLabelNode)->NodeText.ToString().Trim();
            if (!string.Equals(discardLabel, "Discard", StringComparison.OrdinalIgnoreCase)) return -1;
            if (!string.Equals(riichiLabel.TrimEnd('!'), "Riichi", StringComparison.OrdinalIgnoreCase)) return -1;

            if (tileNode == null || (int)tileNode->Type < 1000 || !tileNode->IsVisible()) return -1;
            if (singleTileNode != null && singleTileNode->IsVisible()) return -1;
            var tileComponent = (AtkComponentNode*)tileNode;
            if (tileComponent->Component == null) return -1;
            var nested = &tileComponent->Component->UldManager;
            if (nested->NodeList == null || nested->NodeListCount < 4) return -1;
            var imageNode = nested->NodeList[3];
            if (imageNode == null || imageNode->Type != NodeType.Image || !imageNode->IsVisible())
                return -1;
            var (icon, _) = HandTileScanner.ReadImageIcon((AtkImageNode*)imageNode);
            return icon;
        }
        catch { return -1; }
    }

    // Doman Mahjong icon sequence. The three previously observed mappings
    // (76049, 76068, 76069) are confirmed by local screenshots. Other names
    // follow the contiguous Doman Mahjong icon range and should be verified
    // with further visual comparisons before any gameplay-action features.
    public static string IconToKnownTile(int iconId)
    {
        if (iconId >= 76041 && iconId <= 76049)
        {
            int rank = iconId - 76040;
            return $"{rank} Characters ({rank} Man)";
        }
        if (iconId >= 76050 && iconId <= 76058)
        {
            int rank = iconId - 76049;
            return $"{rank} Circles ({rank} Pin)";
        }
        if (iconId >= 76059 && iconId <= 76067)
        {
            int rank = iconId - 76058;
            return $"{rank} Bamboo ({rank} Sou)";
        }
        return iconId switch
        {
            76075 => "Red 5 Characters (aka 5 Man)",
            76076 => "Red 5 Circles (aka 5 Pin)",
            76077 => "Red 5 Bamboo (aka 5 Sou)",
            76068 => "East wind",
            76069 => "South wind",
            76070 => "West wind",
            76071 => "North wind",
            76072 => "White dragon",
            76073 => "Green dragon",
            76074 => "Red dragon",
            >= 76040 and <= 76079 => $"Unidentified Mahjong icon ({iconId})",
            _ => "(none)",
        };
    }

    public static AddonSnapshot Take(IGameGui gameGui)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        string name = "(none)";
        AtkUnitBase* addon = null;

        // Some regional game clients use Emj, others EmjL.
        foreach (var candidate in new[] { "Emj", "EmjL" })
        {
            addon = gameGui.GetAddonByName<AtkUnitBase>(candidate);
            if (addon == null || !addon->IsVisible) continue;
            name = candidate;
            break;
        }

        foreach (var candidate in new[] { "ContentsFinder", "ContentsFinderConfirm" })
        {
            var finder = gameGui.GetAddonByName<AtkUnitBase>(candidate);
            entries[$"OtherAddon/{candidate}"] =
                finder != null && finder->IsVisible ? "visible" : "not visible";
        }

        if (name == "(none)")
        {
            entries["Status"] = "No active Mahjong table addon";
            return new AddonSnapshot(name, entries);
        }

        entries["Mahjong/Addon"] = name;
        entries["Mahjong/AtkValuesCount"] = addon->AtkValuesCount.ToString();
        entries["Mahjong/NodeListCount"] = addon->UldManager.NodeListCount.ToString();

        if (addon->AtkValues != null)
        {
            for (int i = 0; i < Math.Min((int)addon->AtkValuesCount, 512); i++)
            {
                var value = &addon->AtkValues[i];
                // GetValueAsString is provided by FFXIVClientStructs and handles the
                // differing AtkValue variants without guessing a union member.
                string rendered;
                try { rendered = value->GetValueAsString(); }
                catch (Exception e) { rendered = "<unreadable: " + e.GetType().Name + ">"; }
                entries[$"AtkValue/{i:000}"] = $"{value->Type}: {rendered}";
            }
        }

        if (addon->UldManager.NodeList != null)
        {
            for (int i = 0; i < Math.Min((int)addon->UldManager.NodeListCount, 1000); i++)
            {
                var node = addon->UldManager.NodeList[i];
                var path = $"Root/{i:000}";
                ReadNode(entries, path, node);

                // The initial hint-on/off captures isolated Node ID 45, an
                // AtkComponentNode (type 1027). Inspect its nested node lists
                // so a visual hint can be correlated with text/image parts.
                // This is passive, and restricted to this confirmed candidate.
                if (node != null && node->NodeId == 45 && (int)node->Type >= 1000)
                    ReadComponentChildren(entries, path + "/HintComponent", node, 3);
            }
        }

        // Known candidate tile container IDs, from published MahjongXIV diagnostics.
        // Presence does not establish where *hints* are stored.
        for (int i = 0; i < 14; i++)
        {
            uint nodeId = i switch
            {
                0 => 134u,
                13 => 135u,
                _ => 1340000u + (uint)i,
            };
            var componentNode = addon->GetComponentNodeById(nodeId);
            string prefix = $"Hand/{i:00}/id{nodeId}";
            if (componentNode == null)
            {
                entries[prefix] = "missing";
                continue;
            }
            ReadNode(entries, prefix, (AtkResNode*)componentNode);
            if (componentNode->Component == null)
            {
                entries[prefix + "/Component"] = "null";
                continue;
            }
            var uld = &componentNode->Component->UldManager;
            if (uld->NodeList == null) continue;
            for (int j = 0; j < Math.Min((int)uld->NodeListCount, 96); j++)
                ReadNode(entries, $"{prefix}/child{j:00}", uld->NodeList[j]);
            // Include the button's child images/texture resources, not just
            // the outer tile component. This is where icons are expected.
            ReadComponentChildren(entries, prefix + "/Deep", (AtkResNode*)componentNode, 2);
        }

        return new AddonSnapshot(name, entries);
    }

    private static void ReadComponentChildren(Dictionary<string, string> entries,
        string path, AtkResNode* componentResNode, int depth)
    {
        if (componentResNode == null || depth <= 0 || (int)componentResNode->Type < 1000)
            return;

        var componentNode = (AtkComponentNode*)componentResNode;
        if (componentNode->Component == null)
        {
            entries[path] = "Component=null";
            return;
        }

        var manager = &componentNode->Component->UldManager;
        entries[path + "/NodeCount"] = manager->NodeListCount.ToString();
        if (manager->NodeList == null) return;

        for (int i = 0; i < Math.Min((int)manager->NodeListCount, 128); i++)
        {
            var child = manager->NodeList[i];
            var childPath = $"{path}/child{i:000}";
            ReadNode(entries, childPath, child);
            if (child != null && (int)child->Type >= 1000)
                ReadComponentChildren(entries, childPath + "/Nested", child, depth - 1);
        }
    }

    private static void ReadNode(Dictionary<string, string> map, string key, AtkResNode* node)
    {
        if (node == null) { map[key] = "null"; return; }
        var details = $"id={node->NodeId} type={node->Type} visible={node->IsVisible()} " +
            $"flags={node->NodeFlags} drawFlags=0x{node->DrawFlags:X} " +
            $"alpha={node->Color.A} mulRGB=({node->MultiplyRed},{node->MultiplyGreen},{node->MultiplyBlue}) " +
            $"addRGB=({node->AddRed},{node->AddGreen},{node->AddBlue}) " +
            $"xy=({node->X:F1},{node->Y:F1}) size=({node->Width},{node->Height})";

        // Text content and image part IDs can distinguish two hints that
        // occupy the same space and have identical visibility/colour flags.
        if (node->Type == NodeType.Text)
        {
            try
            {
                var value = ((AtkTextNode*)node)->NodeText.ToString()
                    .Replace('\r', ' ').Replace('\n', ' ');
                details += $" text=\"{value}\"";
            }
            catch (Exception e) { details += $" text=<unreadable:{e.GetType().Name}>"; }
        }
        else if (node->Type == NodeType.Image)
        {
            details += $" imagePartId={((AtkImageNode*)node)->PartId}";
            details += " " + HandTileScanner.DescribeImage((AtkImageNode*)node);
        }
        map[key] = details;
    }

    public string AsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Doman Hint Pilot: passive addon snapshot");
        sb.AppendLine("# Mahjong addon: " + AddonName);
        sb.AppendLine("# Do not assume the values correspond to a verified recommendation.");
        foreach (var (key, value) in Entries.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.AppendLine(key + " = " + value);
        return sb.ToString();
    }

    public static string Compare(AddonSnapshot before, AddonSnapshot after)
    {
        var keys = before.Entries.Keys.Union(after.Entries.Keys).OrderBy(x => x, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("# Differences only: hints off -> hints on (not necessarily all hint-related)");
        int count = 0;
        foreach (var key in keys)
        {
            before.Entries.TryGetValue(key, out var a);
            after.Entries.TryGetValue(key, out var b);
            if (a == b) continue;
            count++;
            sb.AppendLine(key);
            sb.AppendLine("  OFF: " + (a ?? "<absent>"));
            sb.AppendLine("  ON:  " + (b ?? "<absent>"));
        }
        sb.AppendLine("# Changed entries: " + count);
        return sb.ToString();
    }
}

/// <summary>
/// A live hint reading. IsDiscardHint means the panel is visible and the observed
/// data has a plausible discard action/icon; it does not verify which hand slot to act on.
/// </summary>
public readonly record struct HintReading(
    string Addon, int State, bool PanelVisible, string Action,
    int IconId, string TileLabel, bool IsDiscardHint, string Source = "none")
{
    public static HintReading None => new("(none)", -1, false, "", -1, "(none)", false);
}
