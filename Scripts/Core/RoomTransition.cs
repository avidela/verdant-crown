using Godot;
using VerdantCrown.Data;

namespace VerdantCrown.Core;

/// <summary>
/// Area3D exit trigger placed on a room's arch, plus the name-based discovery that wires
/// a whole room: arches are found by their actual node names in the level scenes —
/// "EntryArch" → exits[0] (the way back), "ExitArch" → exits[1] (the way on), and
/// "&lt;TargetRoomPascal&gt;Entrance" (Overworld01's "Dungeon01Entrance") → that room id.
/// When the player (group <see cref="RoomLoader.PlayerGroup"/>, physics layer
/// <see cref="RoomLoader.PlayerCollisionLayer"/>) crosses the thin trigger box, the
/// transition asks the loader to swap rooms; a denied gate is logged "Locked" there
/// (HUD message comes in a later UI pass).
/// <para>The trigger box (3 × 3 × 0.6 m, centred 1.5 m above the arch origin) is thin
/// enough that spawning the player at PlayerStart — 1.2 m from the arch in every dungeon
/// scene — never fires it, so arrivals can't ping-pong back the way they came.</para>
/// <para><b>Entry latch (Sprint 2.7 — held-back cascade fix):</b> triggers that resolve to
/// the room's exits[0] (the way back — EntryArch, and Overworld01's Dungeon01Entrance)
/// start each load DISARMED and only arm once the toward-arch movement input
/// (move_down for an arch at +Z, move_up for −Z, derived from the arch's position
/// relative to the room centre) has been released — a one-way latch. While disarmed a
/// BodyEntered is ignored and logged once per suppression, so holding "back" on arrival
/// can no longer chain d03→d02→d01→overworld. Arming never fires an already-overlapping
/// body: the player must exit the footprint and re-enter (fresh enter). Exit-side
/// (forward) triggers never latch — the pilot's EXIT walk fires immediately.</para>
/// </summary>
public partial class RoomTransition : Area3D
{
    // --- Arch naming (observed in Scenes/Levels/*.tscn) ---------------------
    private const string EntryArchName = "EntryArch";
    private const string ExitArchName = "ExitArch";
    private const string EntranceSuffix = "Entrance";
    private const string ArchSuffix = "Arch";

    // --- Trigger geometry (every arch faces ±Z; verified against the scenes) -
    private const float TriggerWidth = 3.0f;        // spans both pillars (|x| ≤ 1.5)
    private const float TriggerHeight = 3.0f;       // floor → above lintel
    private const float TriggerDepth = 0.6f;        // thin: the spawn point stays outside
    private const float TriggerHeightOffset = 1.5f; // arch origin sits on the floor

    // --- Entry-latch input (same action names PlayerController reads) -------
    private const string MoveUpAction = "move_up";   // maps to −Z (PlayerController)
    private const string MoveDownAction = "move_down"; // maps to +Z

    /// <summary>rooms.json id of the room this arch leads to; assigned before the node enters the tree.</summary>
    public string TargetRoomId { get; set; } = string.Empty;

    /// <summary>True when this trigger resolves to the room's exits[0] — the entry-side latch applies.</summary>
    public bool IsEntrySide { get; private set; }

    /// <summary>+1 when the arch sits toward +Z of the room centre, −1 for −Z, 0 when unknown (latch waits for both axes released).</summary>
    public int ArchSideSign { get; private set; }

    private RoomLoader? _loader;
    private bool _latchArmed; // one-way: false until the toward-arch input is released once

    /// <summary>Wires the owning loader; must be called before the node enters the tree.</summary>
    public void Bind(RoomLoader loader)
    {
        _loader = loader;
    }

    /// <summary>Discovers every arch node in <paramref name="room"/> by name and attaches a trigger to it.</summary>
    public static void AttachAll(Node room, RoomRecord record, RoomLoader loader)
    {
        float roomCentreZ = room is Node3D roomRoot ? roomRoot.GlobalPosition.Z : 0f;

        foreach (Node child in room.GetChildren())
        {
            string target = ResolveArchTarget(child.Name.ToString() ?? string.Empty, record);
            if (target.Length == 0)
            {
                continue; // not an exit arch (Floor, Rocks, lights, ...)
            }

            if (child is not Node3D arch)
            {
                GD.PrintErr($"RoomTransition: arch '{child.Name}' in '{record.Id}' is not a Node3D — skipped.");
                continue;
            }

            // Entry-side = the way back: any arch resolved to exits[0] (EntryArch in every
            // dungeon, and Overworld01's Dungeon01Entrance, which maps to overworld's exits[0]).
            bool isEntrySide = record.Exits is { Count: > 0 } && target == record.Exits[0].RoomId;
            float archOffsetZ = arch.GlobalPosition.Z - roomCentreZ;

            var trigger = new RoomTransition
            {
                Name = $"Transition_{target}",
                TargetRoomId = target,
                IsEntrySide = isEntrySide,
                ArchSideSign = archOffsetZ > 0f ? 1 : archOffsetZ < 0f ? -1 : 0,
            };
            trigger.Bind(loader);
            trigger.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D
                {
                    Size = new Vector3(TriggerWidth, TriggerHeight, TriggerDepth),
                },
            });
            trigger.Position = new Vector3(0f, TriggerHeightOffset, 0f);
            arch.AddChild(trigger);
        }
    }

    public override void _Ready()
    {
        // Detect bodies only — the area reports nothing itself and listens for the player layer.
        CollisionLayer = 0;
        CollisionMask = RoomLoader.PlayerCollisionLayer;
        Monitoring = true;
        BodyEntered += OnBodyEntered;

        // Only entry-side triggers poll input (the latch); exit-side triggers keep the
        // pilot's immediate-fire behaviour untouched.
        SetPhysicsProcess(IsEntrySide);
    }

    public override void _ExitTree()
    {
        BodyEntered -= OnBodyEntered;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_latchArmed)
        {
            SetPhysicsProcess(false); // one-way latch — stop polling
            return;
        }

        // "Toward the arch" in world Z: arch at +Z → move_down (+Z); arch at −Z → move_up (−Z).
        // Unknown side (arch exactly at the centre — never observed) requires both released.
        bool towardArchHeld = ArchSideSign > 0
            ? Input.IsActionPressed(MoveDownAction)
            : ArchSideSign < 0
                ? Input.IsActionPressed(MoveUpAction)
                : Input.IsActionPressed(MoveUpAction) || Input.IsActionPressed(MoveDownAction);

        if (!towardArchHeld)
        {
            _latchArmed = true; // arms once per load; a body already inside must still exit and re-enter
        }
    }

    private void OnBodyEntered(Node3D body)
    {
        if (_loader is null || TargetRoomId.Length == 0)
        {
            return;
        }

        if (!body.IsInGroup(RoomLoader.PlayerGroup))
        {
            return;
        }

        // Entry latch: a held "back" input on arrival must not chain rooms. Suppressed
        // fires are ignored until the latch arms AND only a fresh enter (exit + re-enter)
        // can then trigger — arming never replays an overlap already in progress.
        if (IsEntrySide && !_latchArmed)
        {
            GD.Print($"transition: entry latch suppress -> {TargetRoomId}"); // once per suppression
            return;
        }

        // The loader performs the Progression.CanEnter gate and the actual scene swap
        // (deferred out of this physics callback); locked paths are logged there.
        _loader.RequestTransition(TargetRoomId);
    }

    /// <summary>Maps one node name to a target room id; empty when the node is not an exit arch.</summary>
    private static string ResolveArchTarget(string name, RoomRecord record)
    {
        if (name == EntryArchName || name == ExitArchName)
        {
            int exitIndex = name == EntryArchName ? 0 : 1;
            if (record.Exits is null || exitIndex >= record.Exits.Count)
            {
                GD.PrintErr($"RoomTransition: arch '{name}' in '{record.Id}' has no exits[{exitIndex}] in rooms.json.");
                return string.Empty;
            }

            return record.Exits[exitIndex].RoomId;
        }

        if (name.EndsWith(EntranceSuffix, StringComparison.Ordinal))
        {
            // "<TargetPascal>Entrance" → that room id ("Dungeon01Entrance" → dungeon_01).
            foreach (RoomRecord candidate in GameData.Rooms)
            {
                if (name == RoomLoader.ToPascalCase(candidate.Id) + EntranceSuffix)
                {
                    return candidate.Id;
                }
            }

            GD.PrintErr($"RoomTransition: entrance '{name}' in '{record.Id}' matches no room id — skipped.");
            return string.Empty;
        }

        if (name.EndsWith(ArchSuffix, StringComparison.Ordinal))
        {
            GD.PrintErr($"RoomTransition: unknown arch name '{name}' in '{record.Id}' — skipped.");
        }

        return string.Empty;
    }
}
