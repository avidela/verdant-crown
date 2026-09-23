namespace VerdantCrown.Data;

/// <summary>One roster entry of enemies.json (only the fields the game layer reads today).</summary>
public sealed record EnemyRecord
{
    /// <summary>Stable id (e.g. "slime", "boss_guardian").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name (e.g. "Crown Guardian").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Hit points before defeat (enemies.json hp).</summary>
    public int Hp { get; init; }

    /// <summary>Flat damage per successful hit (enemies.json damage).</summary>
    public int Damage { get; init; }

    /// <summary>Ground speed on the XZ plane in m/s (enemies.json speed_mps); stays below the player's move speed.</summary>
    public float SpeedMps { get; init; }

    /// <summary>Distance in metres at which the enemy notices the player (enemies.json aggro_range_m).</summary>
    public float AggroRangeM { get; init; }

    /// <summary>AI behavior tag (enemies.json behavior — "wander", "charge_player", "boss_swing").</summary>
    public string Behavior { get; init; } = string.Empty;

    /// <summary>Boss phase table (enemies.json phases); empty for regular enemies.</summary>
    public List<EnemyPhaseRecord> Phases { get; init; } = new();
}
