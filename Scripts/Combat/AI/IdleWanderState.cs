using Godot;
using Kit;

namespace VerdantCrown.Combat.AI;

/// <summary>
/// Idle/Wander state of the enemy FSM: pause at the anchor, then amble to a random point
/// within <see cref="WanderRadiusMeters"/> of <see cref="EnemyBrain.HomePosition"/>. As soon
/// as the player steps inside the record's aggro range, hands over to Chase. The repath
/// timer re-picks a target even when walls block the current one, so enemies never wedge.
/// </summary>
public sealed class IdleWanderState : IState<EnemyBrain>
{
    private const float WanderRadiusMeters = 2.5f;   // amble circle around the spawn anchor
    private const float WanderReachMeters = 0.4f;    // distance that counts as "arrived"
    private const float WanderRepathSeconds = 6.0f;  // max seconds walking one target
    private const float WanderPauseMinSeconds = 0.6f;
    private const float WanderPauseMaxSeconds = 1.8f;

    private Vector3 _target;
    private float _pauseRemaining;
    private float _repathRemaining;

    public float TimeInState { get; set; }

    public void Enter(EnemyBrain context)
    {
        _target = context.HomePosition;
        _pauseRemaining = WanderPauseMinSeconds;
        _repathRemaining = WanderRepathSeconds;
    }

    public void Update(EnemyBrain context, float delta)
    {
        Vector3? playerPosition = context.PlayerPosition;
        if (playerPosition is not null
            && context.DistanceToPlayer(playerPosition.Value) <= context.AggroRangeMeters)
        {
            context.TransitionTo(EnemyBrain.StateChase);
            return;
        }

        if (_pauseRemaining > 0f)
        {
            _pauseRemaining -= delta;
            context.Halt(delta);
            return;
        }

        _repathRemaining -= delta;
        bool reachedTarget = HorizontalDistance(context.GlobalPosition, _target) <= WanderReachMeters;
        if (reachedTarget || _repathRemaining <= 0f)
        {
            _target = PickWanderPoint(context.HomePosition);
            _pauseRemaining = (float)GD.RandRange(WanderPauseMinSeconds, WanderPauseMaxSeconds);
            _repathRemaining = WanderRepathSeconds;
            context.Halt(delta);
            return;
        }

        context.MoveToward(_target, delta);
    }

    public void Exit(EnemyBrain context)
    {
        // Nothing to unwind — the next state takes over movement immediately.
    }

    private static Vector3 PickWanderPoint(Vector3 home) => home + new Vector3(
        (float)GD.RandRange(-WanderRadiusMeters, WanderRadiusMeters), 0f,
        (float)GD.RandRange(-WanderRadiusMeters, WanderRadiusMeters));

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.Y = 0f;
        return delta.Length();
    }
}
