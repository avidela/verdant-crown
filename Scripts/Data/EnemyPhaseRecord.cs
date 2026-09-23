namespace VerdantCrown.Data;

/// <summary>One entry of an enemy's <c>phases</c> array in enemies.json (boss phase logic).</summary>
public sealed record EnemyPhaseRecord
{
    /// <summary>Phase number, ascending (1 = default, 2 = first transition, ...).</summary>
    public int Phase { get; init; }

    /// <summary>
    /// Enter this phase when the enemy's current HP percentage is at or below this value
    /// (enemies.json hp_threshold_percent — e.g. 50 means "at ≤ 50 % of max HP").
    /// </summary>
    public int HpThresholdPercent { get; init; }

    /// <summary> Movement speed in m/s while this phase is active (0 = keep the current speed).</summary>
    public float SpeedMps { get; init; }

    /// <summary>Behavior tag while active (e.g. "boss_swing_aggressive"); empty = keep current.</summary>
    public string Behavior { get; init; } = string.Empty;

    /// <summary>Edge action fired once when the phase is applied ("spawn"), or empty for none.</summary>
    public string OnPhaseChange { get; init; } = string.Empty;

    /// <summary>Spawn payload for on_phase_change "spawn"; null when the phase spawns nothing.</summary>
    public EnemySpawnRecord? Spawn { get; init; }
}
