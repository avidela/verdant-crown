namespace VerdantCrown.Data;

/// <summary>One entry of rooms.json: identity, display name, type, exits, win item, gate.</summary>
public sealed record RoomRecord
{
    /// <summary>Stable id used everywhere in code and data (e.g. "dungeon_01").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Player-facing name (e.g. "Ruined Vestibule").</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>rooms.json room_type — "overworld", "dungeon" or "boss".</summary>
    public string RoomType { get; init; } = string.Empty;

    /// <summary>Arch destinations declared for this room (index 0 = the way back).</summary>
    public List<ExitRecord> Exits { get; init; } = new();

    /// <summary>Room items (heart/key pickups, the boss-defeated crown fragment).</summary>
    public List<RoomItemRecord> Items { get; init; } = new();

    /// <summary>Room id whose clear flag opens this room; null = open from the start.</summary>
    public string? LockedBy { get; init; }
}
