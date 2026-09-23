using System;
using System.Collections.Generic;
using System.Linq;
using VerdantCrown.Data;

namespace VerdantCrown.Core;

/// <summary>
/// Pure C# progression tracker (no Godot dependencies): clear flags, locked exits,
/// key items and the boss/win flags — all ids and gates come from
/// <c>Data/rooms.json</c> + <c>Data/progression.json</c>, never from constants.
/// <para>Gate rule (rooms.json notes): an exit is passable when the destination room's
/// <c>locked_by</c> room has been cleared; <c>locked_by: null</c> means open from the start,
/// and <c>exits.locked</c> carries the initial lock state at a fresh run.</para>
/// </summary>
public sealed class Progression
{
    /// <summary>Suffix of the per-room clear flag ("dungeon_01_cleared"), per progression.json.</summary>
    private const string ClearedFlagSuffix = "_cleared";

    /// <summary>Prefix of collected-item flags ("item_crown_fragment_collected"), per progression.json win requires.</summary>
    private const string ItemFlagPrefix = "item_";

    /// <summary>Suffix of collected-item flags ("..._collected").</summary>
    private const string CollectedFlagSuffix = "_collected";

    /// <summary>Used only when progression.json is missing entirely, so the slice stays winnable.</summary>
    private const string FallbackBossDefeatedFlag = "boss_guardian_defeated";

    private readonly Dictionary<string, RoomRecord> _roomsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _clearedFlags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keyItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _flagUnlocks = new(StringComparer.Ordinal);
    private readonly string _bossDefeatedFlag;

    /// <param name="rooms">Rooms from rooms.json — the exit/gate topology.</param>
    /// <param name="progression">progression.json (may be null; falls back to safe defaults).</param>
    public Progression(IReadOnlyList<RoomRecord> rooms, ProgressionRecord? progression)
    {
        if (rooms is not null)
        {
            foreach (RoomRecord room in rooms)
            {
                if (room.Id.Length > 0)
                {
                    _roomsById.TryAdd(room.Id, room);
                }
            }
        }

        if (progression?.ClearFlags is not null)
        {
            foreach (ClearFlagRecord record in progression.ClearFlags)
            {
                if (record.Flag.Length > 0)
                {
                    _flagUnlocks[record.Flag] = record.Unlocks;
                }
            }
        }

        _bossDefeatedFlag = ResolveBossDefeatedFlag(progression);
    }

    /// <summary>True once the boss-guardian flag (progression.json win requires) is set.</summary>
    public bool BossDefeated => _clearedFlags.Contains(_bossDefeatedFlag);

    /// <summary>
    /// True when the player may walk from <paramref name="fromRoom"/> to
    /// <paramref name="toRoom"/>: a declared exit that is either initially unlocked or
    /// whose destination room's <c>locked_by</c> flag has been cleared. Unknown rooms or
    /// undeclared exits are denied.
    /// </summary>
    public bool CanEnter(string fromRoom, string toRoom)
    {
        if (string.IsNullOrEmpty(fromRoom) || string.IsNullOrEmpty(toRoom))
        {
            return false;
        }

        if (!_roomsById.TryGetValue(fromRoom, out RoomRecord? from) || from.Exits is null)
        {
            return false;
        }

        ExitRecord? exit = null;
        foreach (ExitRecord candidate in from.Exits)
        {
            if (candidate.RoomId == toRoom)
            {
                exit = candidate;
                break;
            }
        }

        if (exit is null || !_roomsById.TryGetValue(toRoom, out RoomRecord? to))
        {
            return false; // not a declared path (or a dangling id) — deny, don't guess
        }

        if (!exit.Locked)
        {
            return true; // exit declared open at a fresh run
        }

        if (to.LockedBy is null)
        {
            return true; // rooms.json: locked_by null ⇒ open from the start
        }

        // locked_by names the GATING ROOM; the set stores its "<roomId>_cleared" flag.
        return IsCleared(to.LockedBy);
    }

    /// <summary>Sets a room's clear flag ("&lt;roomId&gt;_cleared"). Idempotent.</summary>
    public void MarkCleared(string roomId)
    {
        if (string.IsNullOrEmpty(roomId))
        {
            return;
        }

        _clearedFlags.Add(roomId + ClearedFlagSuffix);
    }

    /// <summary>True when the room's clear flag is already set.</summary>
    public bool IsCleared(string roomId)
    {
        return !string.IsNullOrEmpty(roomId) && _clearedFlags.Contains(roomId + ClearedFlagSuffix);
    }

    /// <summary>Rooms/items the given room's clear flag opens (from progression.json).</summary>
    public IReadOnlyList<string> GetUnlocksForRoom(string roomId)
    {
        if (!string.IsNullOrEmpty(roomId)
            && _flagUnlocks.TryGetValue(roomId + ClearedFlagSuffix, out IReadOnlyList<string>? unlocks))
        {
            return unlocks;
        }

        return Array.Empty<string>();
    }

    /// <summary>Sets the boss-defeated flag (called when a boss-type room clears).</summary>
    public void MarkBossDefeated()
    {
        _clearedFlags.Add(_bossDefeatedFlag);
    }

    /// <summary>True when the player holds the given key item (e.g. "temple_key").</summary>
    public bool HasKey(string itemId)
    {
        return !string.IsNullOrEmpty(itemId) && _keyItems.Contains(itemId);
    }

    /// <summary>Grants a key item (e.g. "temple_key" when its pickup is wired).</summary>
    public void AddKeyItem(string itemId)
    {
        if (!string.IsNullOrEmpty(itemId))
        {
            _keyItems.Add(itemId);
        }
    }

    /// <summary>Records an item collection as "item_&lt;itemId&gt;_collected" (win requires).</summary>
    public void MarkCollected(string itemId)
    {
        if (!string.IsNullOrEmpty(itemId))
        {
            _clearedFlags.Add(ItemFlagPrefix + itemId + CollectedFlagSuffix);
        }
    }

    /// <summary>
    /// The boss flag is the non-item entry of win_condition.requires (data:
    /// "boss_guardian_defeated"); falls back to the documented id if progression.json
    /// is unavailable so the boss gate still works.
    /// </summary>
    private static string ResolveBossDefeatedFlag(ProgressionRecord? progression)
    {
        string? candidate = progression?.WinCondition?.Requires?
            .FirstOrDefault(required => !required.StartsWith(ItemFlagPrefix, StringComparison.Ordinal));

        return string.IsNullOrEmpty(candidate) ? FallbackBossDefeatedFlag : candidate;
    }
}
