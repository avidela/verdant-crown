namespace VerdantCrown.Data;

/// <summary>The progression.json document: win/lose conditions and the clear-flag chain.</summary>
public sealed record ProgressionRecord
{
    /// <summary>Win condition (collect the crown fragment in dungeon_05).</summary>
    public WinConditionRecord? WinCondition { get; init; }

    /// <summary>Lose condition (player HP depleted → GameOver).</summary>
    public LoseConditionRecord? LoseCondition { get; init; }

    /// <summary>All defined clear flags and what each one unlocks.</summary>
    public List<ClearFlagRecord> ClearFlags { get; init; } = new();
}
