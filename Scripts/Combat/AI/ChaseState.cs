using Godot;
using Kit;

namespace VerdantCrown.Combat.AI;

/// <summary>
/// Chase state: walks straight at the player at the record's speed_mps (skeleton 3.5 closes
/// fast, slime 1.5 ambles — the data decides). Transitions to Attack inside contact range,
/// back to Idle beyond aggro × hysteresis, and to Idle whenever the player is gone
/// (room swap, cutscene, death).
/// </summary>
public sealed class ChaseState : IState<EnemyBrain>
{
    public float TimeInState { get; set; }

    public void Enter(EnemyBrain context)
    {
        // No unwind needed — movement starts on the first Update.
    }

    public void Update(EnemyBrain context, float delta)
    {
        Vector3? playerPosition = context.PlayerPosition;
        if (playerPosition is null)
        {
            context.TransitionTo(EnemyBrain.StateIdle);
            return;
        }

        float distance = context.DistanceToPlayer(playerPosition.Value);
        if (distance <= context.ContactRange)
        {
            context.TransitionTo(EnemyBrain.StateAttack);
            return;
        }

        if (distance > context.ChaseExitRange)
        {
            context.TransitionTo(EnemyBrain.StateIdle);
            return;
        }

        context.MoveToward(playerPosition.Value, delta);
    }

    public void Exit(EnemyBrain context)
    {
        // Nothing to unwind — the next state takes over movement immediately.
    }
}
