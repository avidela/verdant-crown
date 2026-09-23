namespace VerdantCrown.Data;

/// <summary>Spawn payload of a boss phase (enemies.json phases[].spawn).</summary>
public sealed record EnemySpawnRecord
{
    /// <summary>enemies.json id of the enemy to spawn (e.g. "slime").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>How many reinforcements to create.</summary>
    public int Count { get; init; }

    /// <summary>True = fire at most once (phases are monotonic, so the edge happens one time).</summary>
    public bool Once { get; init; } = true;
}
