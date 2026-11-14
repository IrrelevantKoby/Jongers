using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DomanHintPilot;

/// <summary>
/// Second-stage Chi meld selector (unique hint match after a confirmed Chi, with manual fallback).
/// The 2026-11-12 13:17:45 live diagnostic captured two distinct choice
/// components under Emj Root/280 (left child 7, right child 6).
/// Their own registered ButtonClick events use params 9 and 10 respectively.
/// The option-value layout and button-to-left/right association derive from
/// one live capture and screenshots. Only a unique exact hint match may be
/// selected, once, after a user-confirmed Chi; never select by position alone.
/// </summary>
internal static unsafe class ChiMeldChoiceTest
{
    public enum Side { Left, Right }

    public readonly record struct ChoiceIdentity(string Fingerprint)
    {
        public override string ToString() => "two-option Chi chooser";
    }

    private const string AddonName = "Emj";
    private const int ObservedState = 30;
    private const int ObservedChooserRootIndex = 280;
    private const int ObservedChoiceComponentType = 1014;

    public static string Read(IGameGui gui) =>
        TryGetIdentity(gui, out _, out var reason) ?
            "Chi chooser ready: TWO choices / LEFT (event 9) or RIGHT (event 10)" : reason;

    public static bool TryGetIdentity(IGameGui gui, out ChoiceIdentity identity, out string reason) =>
        TryLocate(gui, out _, out _, out _, out identity, out reason);

    /// <summary>
    /// Read the three visible tiles from the game hint panel, and compare them
    /// against each two-option Chi meld stored in Emj AtkValues. In the
    /// 2026-11-12 Chi capture: option LEFT = values[4..6], RIGHT = [8..10].
    /// The fourth value in each 4-value group is *not* a Chi meld tile.
    /// Preserve Aka Dora (red 5) as a distinct icon; NEVER normalize it.
    /// This method sends no input and does not guess when both/neither match.
    /// </summary>
    public static bool TryGetHintMatch(IGameGui gui, out ChoiceIdentity identity,
        out Side side, out string detail)
    {
        side = default;
        if (!TryLocate(gui, out var addon, out _, out _, out identity, out detail))
            return false;
        if (!TryReadHintSequence(addon, out var wanted, out detail)) return false;

        var values = addon->AtkValues;
        int[] left = { values[4].Int, values[5].Int, values[6].Int };
        int[] right = { values[8].Int, values[9].Int, values[10].Int };
        bool matchesLeft = SameTiles(wanted, left);
        bool matchesRight = SameTiles(wanted, right);
        string fmt = $"hint={FormatTiles(wanted)}, LEFT={FormatTiles(left)}, RIGHT={FormatTiles(right)}";
        if (matchesLeft == matchesRight)
        {
            detail = (matchesLeft ? "Both Chi choices match; refusing ambiguous choice. " :
                "Neither Chi choice matches the hint; no selection. ") + fmt;
            return false;
        }
        side = matchesLeft ? Side.Left : Side.Right;
        detail = $"Unique recommended Chi meld: {side}. {fmt}";
        return true;
    }

    /// <summary>
    /// Verify both the recommendation and option data again immediately before
    /// dispatching the selected node's own registered ButtonClick. The first
    /// Chi response must already have been explicitly confirmed by the user.
    /// </summary>
    public static bool TryClickHintMatchOnce(IGameGui gui,
        ChoiceIdentity expected, Side expectedSide, out string detail)
    {
        if (!TryGetHintMatch(gui, out var identity, out var side, out detail)) return false;
        if (identity != expected || side != expectedSide)
        {
            detail = "Recommended Chi meld changed while waiting; no event sent.";
            return false;
        }
        return TryClickOnce(gui, expected, side, out detail);
    }

    private static bool TryReadHintSequence(AtkUnitBase* addon, out int[] tiles, out string reason)
    {
        tiles = Array.Empty<int>();
        reason = "Chi hint panel has not been decoded.";
        var hintNode = addon->GetComponentNodeById(45);
        if (hintNode == null || !((AtkResNode*)hintNode)->IsVisible() ||
            hintNode->Component == null) return false;
        var manager = &hintNode->Component->UldManager;
        if (manager->NodeList == null || manager->NodeListCount < 8)
        {
            reason = "Chi hint panel has an unexpected child layout.";
            return false;
        }
        // Captured Root/286/Child/002 = Discard and Child/003 = Chi!.
        if (!ExactText(manager->NodeList[2], "Discard") ||
            !ExactText(manager->NodeList[3], "Chi!"))
        {
            reason = "Chi!/Discard hint labels are not both visible.";
            return false;
        }
        var ids = new int[3];
        // Children 5,6,7 contain the recommended sequence in reverse visual
        // order. Compare as a multiset, so order does not matter.
        for (int k = 0; k < 3; k++)
        {
            int i = 5 + k;
            var node = manager->NodeList[i];
            if (node == null || !node->IsVisible() || (int)node->Type < 1000 ||
                ((AtkComponentNode*)node)->Component == null)
            {
                reason = $"Recommended Chi hint tile component {i} is hidden or missing.";
                return false;
            }
            var parts = &((AtkComponentNode*)node)->Component->UldManager;
            if (parts->NodeList == null || parts->NodeListCount <= 3)
            {
                reason = $"Recommended Chi hint component {i} has no tile image.";
                return false;
            }
            var tile = parts->NodeList[3];
            if (tile == null || tile->Type != NodeType.Image || !tile->IsVisible())
            {
                reason = $"Recommended Chi hint tile image {i} is unavailable.";
                return false;
            }
            var (icon, _) = HandTileScanner.ReadImageIcon((AtkImageNode*)tile);
            if (!IsMahjongIcon(icon))
            {
                reason = $"Recommended Chi tile {i} has unrecognized icon {icon}.";
                return false;
            }
            ids[k] = icon;
        }
        // A single Mahjong sequence is always in a single suit with ranks
        // increasing by one. Reject corrupt or unrelated images.
        if (!IsSequence(ids))
        {
            reason = "The three Chi hint icons are not one valid consecutive suited sequence.";
            return false;
        }
        tiles = ids;
        reason = "Read three visible recommended Chi tiles.";
        return true;
    }

    private static bool ExactText(AtkResNode* node, string expected) =>
        node != null && node->Type == NodeType.Text && node->IsVisible() &&
        string.Equals(((AtkTextNode*)node)->NodeText.ToString().Trim(), expected,
            StringComparison.Ordinal);

    private static bool IsMahjongIcon(int icon) => icon >= 76041 && icon <= 76077;

    private static bool SameTiles(int[] a, int[] b)
    {
        if (a.Length != 3 || b.Length != 3) return false;
        var x = (int[])a.Clone();
        var y = (int[])b.Clone();
        Array.Sort(x);
        Array.Sort(y);
        return x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }

    private static bool IsSequence(int[] icons)
    {
        if (icons.Length != 3) return false;
        // Map Aka (red) 5 to its rank solely for sequence legality, NOT for
        // exact icon matching of the options above.
        var ranks = new int[3];
        int suit = -1;
        for (int i = 0; i < 3; i++)
        {
            int id = icons[i];
            int nextSuit;
            int rank;
            if (id >= 76041 && id <= 76049) { nextSuit = 0; rank = id - 76040; }
            else if (id >= 76050 && id <= 76058) { nextSuit = 1; rank = id - 76049; }
            else if (id >= 76059 && id <= 76067) { nextSuit = 2; rank = id - 76058; }
            else if (id >= 76075 && id <= 76077) { nextSuit = id - 76075; rank = 5; }
            else return false;
            if (suit >= 0 && suit != nextSuit) return false;
            suit = nextSuit;
            ranks[i] = rank;
        }
        Array.Sort(ranks);
        return ranks[0] + 1 == ranks[1] && ranks[1] + 1 == ranks[2];
    }

    private static string FormatTiles(int[] tiles) =>
        string.Join(" / ", Array.ConvertAll(tiles, AddonSnapshot.IconToKnownTile));

    public static bool TryClickOnce(IGameGui gui, ChoiceIdentity expected, Side side, out string detail)
    {
        if (!TryLocate(gui, out var addon, out var leftEvent, out var rightEvent,
                out var current, out detail)) return false;
        if (current != expected)
        {
            detail = "Chi chooser changed since Prepare; no click sent.";
            return false;
        }
        // Event pointers are read fresh from the current game UI. Never cache
        // a pointer or send an invented FireCallback value.
        var chosen = side == Side.Left ? leftEvent : rightEvent;
        if (chosen == null)
        {
            detail = "The selected Chi choice has no registered click event.";
            return false;
        }
        var type = chosen->State.EventType;
        int param = unchecked((int)chosen->Param);
        addon->ReceiveEvent(type, param, chosen);
        detail = $"{AddonName} / Chi {side} combination / {type} param {param}";
        return true;
    }

    private static bool TryLocate(IGameGui gui, out AtkUnitBase* addon,
        out AtkEvent* leftClick, out AtkEvent* rightClick,
        out ChoiceIdentity identity, out string reason)
    {
        addon = null;
        leftClick = null;
        rightClick = null;
        identity = default;
        var current = gui.GetAddonByName<AtkUnitBase>(AddonName);
        if (current == null || !current->IsReady || !current->IsVisible)
        {
            reason = "Emj is not visible and ready.";
            return false;
        }
        if (current->AtkValues == null || current->AtkValuesCount <= 13)
        {
            reason = "Emj has no complete Chi chooser values.";
            return false;
        }
        var values = current->AtkValues;
        if (values[0].Type != AtkValueType.Int || values[0].Int != ObservedState ||
            values[2].Type.ToString().IndexOf("String", StringComparison.Ordinal) < 0 ||
            !string.Equals(values[2].GetValueAsString().Trim(), "Chi", StringComparison.Ordinal) ||
            values[3].Type != AtkValueType.Int || values[3].Int != 2)
        {
            reason = "Not the captured state-30, two-combination Chi chooser.";
            return false;
        }
        // Corroborate the chooser's displayed tile data. A changed prompt
        // must invalidate Prepare even if there are still two options.
        var tileValues = new int[8];
        for (int i = 4; i <= 11; i++)
        {
            if (values[i].Type != AtkValueType.Int ||
                values[i].Int < 76041 || values[i].Int > 76077)
            {
                reason = $"Chi choice value {i} is not an expected tile icon.";
                return false;
            }
            tileValues[i - 4] = values[i].Int;
        }
        string fingerprint = "Emj|30|Chi|2|" + string.Join(",", tileValues);

        var roots = current->UldManager.NodeList;
        if (roots == null || current->UldManager.NodeListCount <= ObservedChooserRootIndex)
        {
            reason = "Chi chooser root 280 is not available.";
            return false;
        }
        var root = roots[ObservedChooserRootIndex];
        if (root == null || (int)root->Type < 1000 || !root->IsVisible() ||
            !root->NodeFlags.HasFlag(NodeFlags.Enabled))
        {
            reason = "Chi chooser root 280 is hidden or not enabled.";
            return false;
        }
        var rootComponent = ((AtkComponentNode*)root)->Component;
        if (rootComponent == null || rootComponent->UldManager.NodeList == null ||
            rootComponent->UldManager.NodeListCount <= 7)
        {
            reason = "Chi chooser component tree is missing child choices.";
            return false;
        }
        var children = rootComponent->UldManager.NodeList;
        // The Cancel label is a strong discriminator from the ordinary
        // Chi/Pass response panel and from post-Chi discard hints.
        var cancel = children[1];
        if (cancel == null || cancel->NodeId != 11 || (int)cancel->Type != 1007 ||
            !cancel->IsVisible() || !cancel->NodeFlags.HasFlag(NodeFlags.Enabled))
        {
            reason = "Chi chooser Cancel control does not match the capture.";
            return false;
        }
        var cancelComponent = ((AtkComponentNode*)cancel)->Component;
        if (cancelComponent == null || cancelComponent->UldManager.NodeList == null ||
            cancelComponent->UldManager.NodeListCount <= 2 ||
            cancelComponent->UldManager.NodeList[2] == null ||
            cancelComponent->UldManager.NodeList[2]->Type != NodeType.Text ||
            !string.Equals(((AtkTextNode*)cancelComponent->UldManager.NodeList[2])->NodeText.ToString().Trim(),
                "Cancel", StringComparison.Ordinal))
        {
            reason = "Chi chooser Cancel label is not visible or changed.";
            return false;
        }

        var left = children[7];
        var right = children[6];
        if (!ValidChoice(left, 5) || !ValidChoice(right, 6) ||
            left->X >= right->X || Math.Abs(left->Y - right->Y) > 1 ||
            left->Width == 0 || right->Width == 0)
        {
            reason = "The two Chi choice components no longer match left/right layout.";
            return false;
        }
        // Don't issue a choice if 3+ candidate buttons are visible, even if
        // the first two still have recognizable IDs.
        int visibleChoiceCount = 0;
        for (int i = 0; i < (int)rootComponent->UldManager.NodeListCount; i++)
        {
            var child = children[i];
            if (child != null && (int)child->Type == ObservedChoiceComponentType &&
                child->IsVisible() && child->NodeFlags.HasFlag(NodeFlags.Enabled))
                visibleChoiceCount++;
        }
        if (visibleChoiceCount != 2)
        {
            reason = $"Expected exactly two enabled Chi choice components; saw {visibleChoiceCount}.";
            return false;
        }
        if (!GetUniqueClick(left, 9, out leftClick) || !GetUniqueClick(right, 10, out rightClick))
        {
            reason = "Missing/ambiguous registered Chi ButtonClick(param 9/10); refusing to guess.";
            return false;
        }
        addon = current;
        identity = new ChoiceIdentity(fingerprint);
        reason = "Two visible Chi meld choices and their exact registered events are ready.";
        return true;
    }

    private static bool ValidChoice(AtkResNode* node, uint expectedNodeId) =>
        node != null && node->NodeId == expectedNodeId &&
        (int)node->Type == ObservedChoiceComponentType &&
        node->IsVisible() && node->NodeFlags.HasFlag(NodeFlags.Enabled) &&
        node->NodeFlags.HasFlag(NodeFlags.EmitsEvents);

    private static bool GetUniqueClick(AtkResNode* node, uint expectedParam, out AtkEvent* chosen)
    {
        chosen = null;
        int found = 0;
        var evt = node->AtkEventManager.Event;
        for (int i = 0; evt != null && i < 30; evt = evt->NextEvent, i++)
        {
            if (evt->State.EventType != AtkEventType.ButtonClick) continue;
            // Reject any surprising event rather than choosing one arbitrarily.
            if (evt->Param != expectedParam) return false;
            chosen = evt;
            found++;
        }
        return found == 1;
    }
}
