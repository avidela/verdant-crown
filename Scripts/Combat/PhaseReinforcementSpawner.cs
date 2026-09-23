using Godot;
using VerdantCrown.Data;

namespace VerdantCrown.Combat;

/// <summary>
/// Factory for boss-phase reinforcements (enemies.json phases[].spawn): clones
/// EnemyBody.tscn next to the phase-changing enemy, binds the spawn's roster id, and
/// places it on a ring around the boss. Local (not global) coordinates are used because
/// owner and reinforcement share a parent — the spawned brain reads its home anchor
/// during _Ready, so the position must be set before AddChild.
/// <para>Spawned units join the tree after RoomLoader's clear-counter wiring, so they are
/// deliberately NOT counted: the room clears on its originally-declared enemies — the
/// phase slime is a distraction, not a gate.</para>
/// </summary>
public static class PhaseReinforcementSpawner
{
    private const string EnemyPrefabPath = "res://Scenes/Prefabs/EnemyBody.tscn";
    private const float SpawnRingRadiusMeters = 1.5f; // local units around the boss

    /// <summary>Creates <paramref name="spawn"/>'s reinforcements; errors are logged, never thrown.</summary>
    public static void Spawn(EnemyBrain owner, EnemySpawnRecord spawn)
    {
        PackedScene? prefab = GD.Load<PackedScene>(EnemyPrefabPath);
        if (prefab is null)
        {
            GD.PrintErr($"{owner.Name}: cannot load '{EnemyPrefabPath}' for phase spawn '{spawn.Id}'.");
            return;
        }

        Node? parent = owner.GetParent();
        if (parent is null)
        {
            GD.PrintErr($"{owner.Name}: no parent to host phase spawn '{spawn.Id}'.");
            return;
        }

        int count = Mathf.Max(spawn.Count, 0);
        for (int i = 0; i < count; i++)
        {
            Node? instance = prefab.Instantiate();
            if (instance is not EnemyBrain reinforcement)
            {
                instance?.QueueFree();
                GD.PrintErr($"{owner.Name}: '{EnemyPrefabPath}' root is not EnemyBrain — spawn skipped.");
                continue;
            }

            reinforcement.EnemyId = spawn.Id;
            reinforcement.Name = $"{spawn.Id}_spawn_{i}";
            float angle = Mathf.Tau * i / count;
            reinforcement.Position = owner.Position
                + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRingRadiusMeters;
            // Phase Evaluate can run while the owner's _Ready is still setting up
            // children (bind-time) — add_child directly fails there and silently eats
            // the once-flag. Deferred add preserves the ordering contract: position and
            // home anchor are already set on the node BEFORE it enters the tree.
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(parent))
                {
                    parent.AddChild(reinforcement); // _Ready binds data + captures the home anchor here
                }
                else
                {
                    reinforcement.Free();
                }
            }).CallDeferred();
        }

        GD.Print($"{owner.Name}: phase spawn — {count}x '{spawn.Id}'.");
    }
}
