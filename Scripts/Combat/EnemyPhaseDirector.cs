using Godot;
using VerdantCrown.Data;

namespace VerdantCrown.Combat;

/// <summary>
/// Applies enemies.json <c>phases[]</c> to one <see cref="EnemyBrain"/>: picks the phase
/// with the smallest hp_threshold_percent that still covers the current HP percentage
/// (shipped data: 100 % → phase 1, ≤ 50 % → phase 2), then applies every not-yet-applied
/// phase up to it in phase order — speed/behavior swap plus the
/// <c>on_phase_change: "spawn"</c> edge through <see cref="PhaseReinforcementSpawner"/>.
/// Monotonic: each phase fires exactly once, so a once:true spawn happens one time.
/// No threshold or percentage is hardcoded; the record decides.
/// </summary>
public sealed class EnemyPhaseDirector
{
    private const string PhaseActionSpawn = "spawn"; // enemies.json phases[].on_phase_change

    private readonly EnemyBrain _owner;
    private readonly int _maxHp;
    private readonly List<EnemyPhaseRecord> _byProgression = new(); // ascending phase number
    private readonly List<EnemyPhaseRecord> _byThreshold = new();   // ascending hp threshold
    private readonly HashSet<int> _applied = new();

    public EnemyPhaseDirector(EnemyBrain owner, EnemyRecord record, int maxHp)
    {
        _owner = owner;
        _maxHp = maxHp;

        foreach (EnemyPhaseRecord phase in record.Phases)
        {
            _byProgression.Add(phase);
            _byThreshold.Add(phase);
        }

        _byProgression.Sort((a, b) => a.Phase.CompareTo(b.Phase));
        _byThreshold.Sort((a, b) => a.HpThresholdPercent.CompareTo(b.HpThresholdPercent));
    }

    /// <summary>Advances phases for <paramref name="currentHp"/>; no-op for phase-less enemies.</summary>
    public void Evaluate(int currentHp)
    {
        if (_maxHp <= 0 || _byThreshold.Count == 0)
        {
            return;
        }

        float hpPercent = 100f * currentHp / _maxHp;
        EnemyPhaseRecord? target = null;
        foreach (EnemyPhaseRecord phase in _byThreshold)
        {
            if (phase.HpThresholdPercent >= hpPercent)
            {
                target = phase;
                break;
            }
        }

        if (target is null)
        {
            return;
        }

        foreach (EnemyPhaseRecord phase in _byProgression)
        {
            if (phase.Phase <= target.Phase && _applied.Add(phase.Phase)) // apply every phase UP TO the reached one (>= booted the boss into its LAST phase at full HP)
            {
                Apply(phase);
            }
        }
    }

    private void Apply(EnemyPhaseRecord phase)
    {
        _owner.SetPhaseSpeed(phase.SpeedMps);
        _owner.SetPhaseBehavior(phase.Behavior);
        GD.Print($"{_owner.Name}: phase {phase.Phase} active — {_owner.CurrentSpeedMps} m/s, '{_owner.Behavior}'.");

        if (phase.OnPhaseChange == PhaseActionSpawn && phase.Spawn is not null)
        {
            PhaseReinforcementSpawner.Spawn(_owner, phase.Spawn);
        }
    }
}
