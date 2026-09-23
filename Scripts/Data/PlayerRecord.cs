namespace VerdantCrown.Data;

/// <summary>The player.json document (only the fields the game layer reads).</summary>
public sealed record PlayerRecord
{
    /// <summary>Record id ("player").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (e.g. "Verdant Hero").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Maximum hit points (player.json hp_max) — seeds <c>PlayerHealth.HpMax</c>.</summary>
    public int HpMax { get; init; }
}
