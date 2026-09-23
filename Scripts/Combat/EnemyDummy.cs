using Godot;
using VerdantCrown.Core;

namespace VerdantCrown.Combat;

/// <summary>
/// Sprint 2 combat-proof damage receiver: a cheap training-dummy enemy.
///
/// Attachment: put this script on a child <c>Node3D</c> of a <c>StaticBody3D</c> or
/// <c>CharacterBody3D</c> enemy (that is how Whitebox.tscn wires it), or directly on
/// any <c>Node3D</c>. The host body is whatever node should die with the dummy:
/// the parent when the parent is the enemy physics body, otherwise this node itself.
///
/// On defeat it prints, publishes <see cref="EventBus.RaisePlayerHudChanged"/>, and
/// frees the host body. Each surviving hit triggers a one-shot scale pulse (cheaper
/// than swapping shared whitebox materials).
/// </summary>
public partial class EnemyDummy : Node3D, IDamageable
{
    // --- Durability ---------------------------------------------------------
    private const int DefaultMaxHp = 3;      // hit points before the dummy is defeated

    // --- Hit feedback (kept deliberately cheap for the whitebox) ------------
    private const float HitPulseScale = 1.15f; // uniform scale multiplier on hit
    private const float HitPulseSeconds = 0.1f; // seconds to settle back to rest scale

    private int _hp = DefaultMaxHp;
    private bool _dead;
    private Vector3 _restScale = Vector3.One;
    private Tween? _pulseTween;

    /// <summary>Hit points remaining; never reported below zero.</summary>
    public int Hp => _hp;

    /// <summary>True once this dummy has been defeated.</summary>
    public bool IsDead => _dead;

    public override void _Ready()
    {
        _restScale = HostNode.Scale;
    }

    /// <summary>Reduces HP, pulses on hit, and frees the host body when HP reaches zero.</summary>
    public void TakeDamage(int amount)
    {
        if (_dead || amount <= 0)
        {
            return;
        }

        _hp -= amount;
        GD.Print($"{Name}: took {amount} damage ({Mathf.Max(_hp, 0)}/{DefaultMaxHp} HP).");

        if (_hp <= 0)
        {
            Die();
            return;
        }

        PulseHost();
    }

    /// <summary>
    /// The node that visually represents this dummy: the parent when the parent is the
    /// enemy physics body (script-on-child wiring), otherwise this node itself.
    /// </summary>
    private Node3D HostNode => GetParent() is CollisionObject3D body ? body : this;

    private void Die()
    {
        _dead = true;
        _pulseTween?.Kill();
        GD.Print($"{Name}: defeated — leaving the arena.");
        EventBus.RaisePlayerHudChanged();

        // Free the enemy body when we hang off one; otherwise free just ourselves.
        if (GetParent() is CollisionObject3D enemyBody)
        {
            enemyBody.QueueFree();
        }
        else
        {
            QueueFree();
        }
    }

    /// <summary>One-shot squash-and-recover pulse on the host body.</summary>
    private void PulseHost()
    {
        Node3D host = HostNode;
        _pulseTween?.Kill();
        host.Scale = _restScale * HitPulseScale;
        _pulseTween = host.CreateTween();
        _pulseTween.TweenProperty(host, "scale", _restScale, HitPulseSeconds)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
    }
}
