namespace VerdantCrown.Data;

/// <summary>One progression.json clear flag: its id and the rooms/items it unlocks.</summary>
public sealed record ClearFlagRecord
{
    /// <summary>Flag id (e.g. "dungeon_03_cleared", "boss_guardian_defeated").</summary>
    public string Flag { get; init; } = string.Empty;

    /// <summary>Room ids / spawns this flag opens once set.</summary>
    public List<string> Unlocks { get; init; } = new();
}
