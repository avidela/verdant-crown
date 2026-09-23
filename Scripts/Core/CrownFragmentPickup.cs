using Godot;
using VerdantCrown.Data;

namespace VerdantCrown.Core;

/// <summary>
/// Win-condition pickup for the vertical slice: wraps the room item whose rooms.json
/// condition is "boss_defeated" (dungeon_05 → crown_fragment → node "CrownFragment")
/// in an Area3D trigger. Touching it while <see cref="Progression.BossDefeated"/> holds
/// collects the item and moves <see cref="GameManager"/> into the win state
/// (progression.json win_condition.result_state — "Victory"), printing "CROWN RECLAIMED".
/// Before the boss falls the pickup is sealed and says so. The full victory screen is a
/// later UI pass; the node stays visible for the whole fight (art/presentation pass will
/// add the conditional spawn effect).
/// <para>Attached by <see cref="RoomLoader"/> when a room declares the conditional item;
/// the node lives inside the room scene and is freed with it on every room swap.</para>
/// </summary>
public partial class CrownFragmentPickup : Node
{
    /// <summary>rooms.json item condition that marks the win item (items[].condition).</summary>
    private const string WinItemCondition = "boss_defeated";

    private const string PickupAreaName = "CrownFragmentPickup";
    private const float PickupRadius = 1.5f;    // fires when the player touches the 1 m cube
    private const float HeightOffset = 1.0f;    // lift the sphere onto the fragment

    private RoomLoader? _loader;
    private Area3D? _area;

    /// <summary>
    /// Finds the room's conditional win item and its scene node (item id in PascalCase:
    /// crown_fragment → CrownFragment), then adds the pickup under the room.
    /// Returns null when the room has no conditional item or wiring failed (errors are logged).
    /// </summary>
    public static CrownFragmentPickup? Attach(Node room, RoomRecord record, RoomLoader loader)
    {
        RoomItemRecord? winItem = null;
        foreach (RoomItemRecord item in record.Items)
        {
            if (item.Condition == WinItemCondition)
            {
                winItem = item;
                break;
            }
        }

        if (winItem is null)
        {
            return null; // no conditional win item in this room — normal for five of six rooms
        }

        WinConditionRecord? win = GameData.Progression?.WinCondition;
        if (win is null || win.RoomId != record.Id)
        {
            GD.PrintErr($"CrownFragmentPickup: win condition missing or not bound to '{record.Id}' — Victory unreachable.");
            return null;
        }

        if (win.ItemId != winItem.Id)
        {
            GD.PrintErr($"CrownFragmentPickup: win item mismatch (progression '{win.ItemId}' vs rooms '{winItem.Id}').");
        }

        string nodeName = RoomLoader.ToPascalCase(winItem.Id);
        Node3D? fragment = room.GetNodeOrNull<Node3D>(nodeName);
        if (fragment is null)
        {
            GD.PrintErr($"CrownFragmentPickup: node '{nodeName}' missing in '{record.Id}' — Victory unreachable.");
            return null;
        }

        var area = new Area3D
        {
            Name = PickupAreaName,
            CollisionLayer = 0,
            CollisionMask = RoomLoader.PlayerCollisionLayer,
            Monitoring = true,
        };
        area.AddChild(new CollisionShape3D
        {
            Shape = new SphereShape3D { Radius = PickupRadius },
        });

        var pickup = new CrownFragmentPickup { Name = PickupAreaName };
        pickup._loader = loader;
        pickup._area = area;
        pickup.AddChild(area);
        room.AddChild(pickup);
        area.GlobalPosition = fragment.GlobalPosition + new Vector3(0f, HeightOffset, 0f);

        area.BodyEntered += body => pickup.OnBodyEntered(body);
        return pickup;
    }

    /// <summary>
    /// Boss-death edge: re-checks one frame later in case the player was already standing
    /// on the fragment when the boss fell (their BodyEntered fired while still sealed).
    /// </summary>
    public void RecheckOverlap()
    {
        if (_area is null || !GodotObject.IsInstanceValid(_area))
        {
            return; // room was swapped before the deferred call ran
        }

        foreach (Node3D body in _area.GetOverlappingBodies())
        {
            TryCollect(body);
        }
    }

    private void OnBodyEntered(Node body)
    {
        TryCollect(body);
    }

    private void TryCollect(Node body)
    {
        if (_loader is null || body is not Node3D node || !node.IsInGroup(RoomLoader.PlayerGroup))
        {
            return;
        }

        WinConditionRecord? win = GameData.Progression?.WinCondition;
        Progression? progression = _loader.Progression;
        if (win is null || progression is null)
        {
            return;
        }

        if (!progression.BossDefeated)
        {
            GD.Print("CrownFragment: sealed until the Crown Guardian is defeated.");
            return;
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null || gameManager.State != GameManager.GameState.Playing)
        {
            return;
        }

        if (win.RoomId != _loader.CurrentRoomId)
        {
            GD.PrintErr($"CrownFragmentPickup: collected outside '{win.RoomId}' (at '{_loader.CurrentRoomId}').");
            return;
        }

        progression.MarkCollected(win.ItemId);

        GameManager.GameState result = GameManager.GameState.Victory;
        if (!string.IsNullOrEmpty(win.ResultState) && !Enum.TryParse(win.ResultState, out result))
        {
            GD.PrintErr($"CrownFragmentPickup: unknown win result_state '{win.ResultState}' — using Victory.");
            result = GameManager.GameState.Victory;
        }

        gameManager.SetState(result);
        GD.Print("CROWN RECLAIMED");
    }
}
