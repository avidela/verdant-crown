using Godot;

namespace VerdantCrown.World;

/// <summary>
/// Heart pickup: attaches an Area3D over its parent heart body; on player contact
/// heals via <see cref="PlayerHealth.Heal"/> and frees the heart.
/// Mirrors Core/CrownFragmentPickup.Attach's runtime-Area3D pattern (scene-author
/// added the `Pickup` marker node without a script — see QA finding #5).
/// </summary>
public partial class HeartPickup : Node3D
{
    private const int HealAmount = 1;
    private const bool HealToFull = true; // slice balance: a heart fully restores — the
                                          // detour through a swarm must be worth its risk
    private const float TriggerSize = 0.7f; // slightly proud of the 0.4 m heart box

    private Area3D? _area;
    private bool _taken;

    public override void _Ready()
    {
        // _Ready during scene setup: adding children to busy parents is illegal — defer.
        CallDeferred(nameof(AttachArea));
    }

    private void AttachArea()
    {
        if (_taken || !IsInstanceValid(this))
        {
            return;
        }

        var host = GetParentOrNull<StaticBody3D>();
        if (host is null)
        {
            GD.PrintErr("HeartPickup: parent is not the heart StaticBody3D — pickup disabled.");
            return;
        }

        _area = new Area3D { CollisionLayer = 0, CollisionMask = 1 }; // layer 1 = player
        var shape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(TriggerSize, TriggerSize, TriggerSize) },
        };
        _area.AddChild(shape);
        host.AddChild(_area);
        _area.BodyEntered += OnBodyEntered;
    }

    private void OnBodyEntered(Node body)
    {
        if (_taken || !body.IsInGroup(Core.RoomLoader.PlayerGroup))
        {
            return;
        }

        PlayerHealth? health = body.GetNodeOrNull<PlayerHealth>("Health");
        if (health is null)
        {
            GD.PrintErr("HeartPickup: player has no Health node — pickup ignored.");
            return;
        }

        if (health.IsDead || health.Hp >= health.HpMax)
        {
            return; // stay on the floor until it can actually heal
        }

        _taken = true;
        health.Heal(HealToFull ? health.HpMax - health.Hp : HealAmount);
        GD.Print($"Heart picked up ({health.Hp}/{health.HpMax})");
        GetParentOrNull<StaticBody3D>()?.QueueFree();
    }
}
