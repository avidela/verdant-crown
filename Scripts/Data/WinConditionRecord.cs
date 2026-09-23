namespace VerdantCrown.Data;

/// <summary>progression.json win_condition: what must happen to reach the win state.</summary>
public sealed record WinConditionRecord
{
    /// <summary>Item to collect (e.g. "crown_fragment").</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>Room the item lives in (e.g. "dungeon_05").</summary>
    public string RoomId { get; init; } = string.Empty;

    /// <summary>Flags required before the win fires; non-item entries identify the boss flag.</summary>
    public List<string> Requires { get; init; } = new();

    /// <summary>GameManager.GameState name to enter on success (e.g. "Victory").</summary>
    public string ResultState { get; init; } = string.Empty;
}
