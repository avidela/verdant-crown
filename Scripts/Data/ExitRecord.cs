namespace VerdantCrown.Data;

/// <summary>One exit of a room in rooms.json: where it leads and its initial lock state.</summary>
public sealed record ExitRecord
{
    /// <summary>Destination room id (e.g. "overworld_01").</summary>
    public string RoomId { get; init; } = string.Empty;

    /// <summary>Initial lock state at a fresh run (JSON: exits.locked).</summary>
    public bool Locked { get; init; }
}
