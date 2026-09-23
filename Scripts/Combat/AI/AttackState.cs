using Godot;
using Kit;

namespace VerdantCrown.Combat.AI;

/// <summary>
/// Attack state: while the player stays inside <see cref="EnemyBrain.ContactRange"/>, deal
/// the record's contact damage every tick and keep pressing forward (MoveAndSlide stops
/// overlap at the capsules). <see cref="World.PlayerHealth.InvulnWindowSeconds"/> debounces
/// the repeats, so one touch = one hit per 0.8 s window. The player stepping out of range
/// drops back to Chase, which re-enters Attack if they stay close.
/// </summary>
public sealed class AttackState : IState<EnemyBrain>
{
    public float TimeInState { get; set; }

    public void Enter(EnemyBrain context)
    {
        // Damage lands in Update so the first frame respects the i-frame window too.
    }

    public void Update(EnemyBrain context, float delta)
    {
        Vector3? playerPosition = context.PlayerPosition;
        if (playerPosition is null)
        {
            context.TransitionTo(EnemyBrain.StateIdle);
            return;
        }

        if (context.DistanceToPlayer(playerPosition.Value) > context.ContactRange)
        {
            context.TransitionTo(EnemyBrain.StateChase);
            return;
        }

        context.TryDealContactDamage();
        context.MoveToward(playerPosition.Value, delta);
    }

    public void Exit(EnemyBrain context)
    {
        // Nothing to unwind — the next state takes over movement immediately.
    }
}
