using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Passive observation of the 13 concealed tiles + separate draw slot.
/// Node IDs and layout are candidates; never send game actions based on this
/// until the contents have been confirmed against a live NPC match.
/// </summary>
public static unsafe class HandTileScanner
{
    public static HandScan Read(IGameGui gameGui, HintReading hint)
    {
        AtkUnitBase* addon = null;
        foreach (var name in new[] { "Emj", "EmjL" })
        {
            addon = gameGui.GetAddonByName<AtkUnitBase>(name);
            if (addon != null && addon->IsVisible) break;
            addon = null;
        }
        if (addon == null)
            return HandScan.Empty;

        var tiles = new List<HandTile>(14);
        for (int slot = 0; slot < 14; slot++)
        {
            uint nodeId = slot switch
            {
                0 => 134u,
                13 => 135u,
                _ => 1340000u + (uint)slot,
            };
            var tileComponent = addon->GetComponentNodeById(nodeId);
            if (tileComponent == null || tileComponent->Component == null || !((AtkResNode*)tileComponent)->IsVisible())
            {
                tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Tile component absent, hidden or not initialized"));
                continue;
            }

            try
            {
                var outerNodes = &tileComponent->Component->UldManager;
                if (outerNodes->NodeList == null || outerNodes->NodeListCount < 1)
                {
                    tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Tile component has no children"));
                    continue;
                }
                var buttonResNode = outerNodes->NodeList[0];
                if (buttonResNode == null || (int)buttonResNode->Type < 1000)
                {
                    tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Child 0 is not a button component"));
                    continue;
                }
                var button = (AtkComponentNode*)buttonResNode;
                if (button->Component == null)
                {
                    tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Button component is null"));
                    continue;
                }
                var buttonNodes = &button->Component->UldManager;
                if (buttonNodes->NodeList == null || buttonNodes->NodeListCount <= 4)
                {
                    tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Button has fewer than five nodes"));
                    continue;
                }
                var imageResNode = buttonNodes->NodeList[4];
                if (imageResNode == null || imageResNode->Type != NodeType.Image)
                {
                    tiles.Add(new HandTile(slot, nodeId, -1, "(not readable)", "Button child 4 is not an image"));
                    continue;
                }
                var image = (AtkImageNode*)imageResNode;
                var (iconId, description) = ReadImageIcon(image);
                tiles.Add(new HandTile(slot, nodeId, iconId,
                    iconId > 0 ? AddonSnapshot.IconToKnownTile(iconId) : "(not readable)", description));
            }
            catch (Exception ex)
            {
                tiles.Add(new HandTile(slot, nodeId, -1, "(read error)", ex.GetType().Name));
            }
        }

        // Respect red-five identity; a base five and a red five are related
        // but are not the same physical tile. Do not collapse them silently.
        var matching = hint.IsDiscardHint
            ? tiles.Where(t => t.IconId == hint.IconId).Select(t => t.Slot).ToList()
            : new List<int>();
        bool fullHand = tiles.Count == 14 && tiles.All(t => t.IconId >= 76041 && t.IconId <= 76077);
        var summary = !hint.IsDiscardHint
            ? "No active discard recommendation; hand scan is diagnostic only."
            : !fullHand
                ? $"Hand scan incomplete: {tiles.Count(t => t.IconId > 0)}/14 tiles recognized. Save scan for diagnosis."
                : matching.Count == 0
                    ? $"All 14 tiles decoded, but no exact match for {hint.IconId} ({hint.TileLabel})."
                    : matching.Count == 1
                        ? $"Candidate match: position {matching[0] + 1} (slot {matching[0]}). Please verify on screen."
                        : $"{matching.Count} identical matches at positions {string.Join(", ", matching.Select(i => i + 1))}.";

        return new HandScan(hint, tiles, matching, fullHand, summary);
    }

    /// <summary>
    /// Reads an icon loaded into an existing UI image. Does not load, patch,
    /// or change any texture. The part ID is an index, not the game icon ID.
    /// </summary>
    internal static (int Icon, string Description) ReadImageIcon(AtkImageNode* image)
    {
        if (image == null) return (-1, "Image is null");
        var partsList = image->PartsList;
        if (partsList == null || partsList->Parts == null)
            return (-1, $"Image part {image->PartId}: no parts list");
        if (image->PartId >= partsList->PartCount || partsList->PartCount > 1024)
            return (-1, $"Image part {image->PartId}: invalid part index/count {partsList->PartCount}");
        var part = &partsList->Parts[image->PartId];
        if (part->UldAsset == null)
            return (-1, "Image has no UldAsset");
        var texture = &part->UldAsset->AtkTexture;
        if (texture->Resource == null)
            return (-1, "Image asset has no texture resource");
        var resource = texture->Resource;
        int iconId = (int)resource->IconId;
        string path = "";
        // Some icons are loaded using an explicit texture path instead of an
        // icon ID; preserve the path for diagnosis, and parse it if possible.
        if (resource->TexFileResourceHandle != null)
        {
            path = resource->TexFileResourceHandle->FileName.ToString();
            if (path.Length > 220) path = path[..220];
        }
        if (iconId < 76041 || iconId > 76077)
        {
            var match = Regex.Match(path, @"(?<!\d)0?(760\d{2})(?=[._])");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
                iconId = parsed;
        }
        string note = $"part={image->PartId}, resourceIcon={resource->IconId}, path='{path}'";
        return iconId >= 76041 && iconId <= 76077 ? (iconId, note) : (-1, note);
    }

    public static string DescribeImage(AtkImageNode* node)
    {
        try
        {
            var (id, desc) = ReadImageIcon(node);
            return $"tileIconId={id} {desc}";
        }
        catch (Exception e) { return $"tileIconError={e.GetType().Name}"; }
    }
}

public readonly record struct HandTile(int Slot, uint NodeId, int IconId, string TileLabel, string Detail);

public sealed record HandScan(HintReading Hint, IReadOnlyList<HandTile> Tiles,
    IReadOnlyList<int> MatchingSlots, bool FullHandRead, string Summary)
{
    public static HandScan Empty => new(HintReading.None, Array.Empty<HandTile>(),
        Array.Empty<int>(), false, "No active Mahjong table.");

    public string AsText()
    {
        var lines = new StringBuilder();
        lines.AppendLine("# Doman Hint Pilot 0.4: read-only hand diagnostic");
        lines.AppendLine("# No inputs or gameplay actions were issued");
        lines.AppendLine($"Addon = {Hint.Addon}");
        lines.AppendLine($"State = {Hint.State}");
        lines.AppendLine($"Recommendation = {Hint.TileLabel}");
        lines.AppendLine($"RecommendedIconId = {Hint.IconId}");
        lines.AppendLine($"IsDiscardHint = {Hint.IsDiscardHint}");
        lines.AppendLine($"All14TilesDecoded = {FullHandRead}");
        lines.AppendLine($"Summary = {Summary}");
        foreach (var tile in Tiles)
        {
            lines.AppendLine($"Slot[{tile.Slot:00}] node={tile.NodeId} icon={tile.IconId} " +
                $"label={tile.TileLabel} detail={tile.Detail}");
        }
        return lines.ToString();
    }
}
