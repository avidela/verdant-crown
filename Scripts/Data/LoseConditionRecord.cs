namespace VerdantCrown.Data;

/// <summary>progression.json lose_condition: what must happen to reach the lose state.</summary>
public sealed record LoseConditionRecord
{
    /// <summary>GameManager.GameState name to enter on loss (e.g. "GameOver").</summary>
    public string ResultState { get; init; } = string.Empty;
}
