using Godot;
using System.Collections.Generic;
using VerdantCrown.Data;

namespace VerdantCrown.Core;

/// <summary>
/// Composition root of the room system: boots <see cref="GameData"/> and
/// <see cref="Progression"/>, loads the starting room (first overworld in rooms.json),
/// keeps the persistent player positioned at each room's PlayerStart, tracks enemy
/// deaths for clear flags, and hosts the win pickup. Arch discovery/triggers live in
/// <see cref="RoomTransition"/>, the Crown Fragment win logic in
/// <see cref="CrownFragmentPickup"/>.
/// <para>Scene path convention: rooms.json id → <c>res://Scenes/Levels/&lt;PascalName&gt;.tscn</c>
/// (overworld_01 → Overworld01, dungeon_05 → Dungeon05). Rooms are instantiated as
/// siblings under Main and replace the previous room. A failed load (missing path or
/// stale-import null) NEVER unloads the current room. The room's areas (transition
/// triggers and win pickup) attach two physics frames AFTER the swap — see
/// <see cref="AttachRoomAreasAfterPlayerSync"/> — so their initial-overlap sweep can
/// never read the previous room's position (Sprint 2.6E phantom-swap fix).</para>
/// </summary>
public partial class RoomLoader : Node
{
    // --- Conventions shared with other game-layer nodes --------------------
    /// <summary>Group on the Player scene root; Hud, RoomTransition and CrownFragmentPickup resolve the player through it.</summary>
    public const string PlayerGroup = "player";

    /// <summary>Physics layer of the player body (Player.tscn root collision_layer = 1).</summary>
    public const uint PlayerCollisionLayer = 1;

    // --- Scene / data naming ----------------------------------------------
    private const string LevelsDir = "res://Scenes/Levels/";
    private const string SceneExtension = ".tscn";
    private const string PlayerStartNodeName = "PlayerStart";
    private const string EnemiesNodeName = "Enemies";
    private const string PlayerSiblingName = "Player"; // fallback lookup next to this node
    private const string OverworldRoomType = "overworld";
    private const string BossRoomType = "boss";

    /// <summary>Room currently instantiated under Main; empty until the first successful load.</summary>
    public string CurrentRoomId { get; private set; } = string.Empty;

    /// <summary>Progression tracker built from Data/*.json; null only before _Ready.</summary>
    public Progression? Progression { get; private set; }

    private Node? _currentRoom;
    private CharacterBody3D? _player;
    private ulong _generation;          // bumped per swap; invalidates old room's callbacks
    private int _aliveEnemies;          // enemies of the current room still in the tree
    private string? _pendingRoomId;     // deferred swap target (dedupe double triggers)
    private CrownFragmentPickup? _fragmentPickup; // win pickup of the current room, if any

    public override void _Ready()
    {
        GameData.EnsureLoaded();
        Progression = new Progression(GameData.Rooms, GameData.Progression);
        LogEnemyRoster();

        _player = ResolvePlayer();

        // add_child is illegal while the parent is still setting up its children
        // (Main is mid-_Ready when RoomLoader._Ready runs) — defer the first load.
        CallDeferred(nameof(BootStartRoom));
    }

    public void BootStartRoom()  // public: Godot's CallDeferred resolves via the generated method bridge
    {
        string startRoom = ChooseStartingRoom();
        if (startRoom.Length == 0)
        {
            GD.PrintErr("RoomLoader: rooms.json declares no starting room — nothing loaded.");
            return;
        }

        if (!LoadRoom(startRoom))
        {
            GD.PrintErr($"RoomLoader: failed to load starting room '{startRoom}'.");
            return;
        }

        EnterPlayingState();
    }

    /// <summary>
    /// Asked by a <see cref="RoomTransition"/> when the player crosses an arch. Checks the
    /// Progression gate immediately (logging "Locked" on denial) and defers the actual
    /// scene swap out of the physics callback.
    /// </summary>
    public bool RequestTransition(string toRoomId)
    {
        if (_pendingRoomId is not null)
        {
            // Double-trigger race: a second fire while the swap is already queued.
            GD.Print($"Refused: {CurrentRoomId} -> {toRoomId} (pending {_pendingRoomId})");
            return false;
        }

        if (CurrentRoomId.Length == 0 || toRoomId.Length == 0)
        {
            return false; // pre-boot / empty target — no room context to log against
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is not null && gameManager.State != GameManager.GameState.Playing)
        {
            // No swaps during Victory/GameOver/... — was silent, now logged with the state.
            GD.Print($"Refused: {CurrentRoomId} -> {toRoomId} (state={(int)gameManager.State})");
            return false;
        }

        if (Progression is null)
        {
            GD.PrintErr("RoomLoader: progression not initialised — transition denied.");
            return false;
        }

        if (!Progression.CanEnter(CurrentRoomId, toRoomId))
        {
            GD.Print($"Locked: {CurrentRoomId} -> {toRoomId}");
            return false;
        }

        _pendingRoomId = toRoomId;
        CallDeferred(nameof(ApplyPendingTransition));
        return true;
    }

    /// <summary>Deferred entry point for <see cref="RequestTransition"/>; runs outside the physics flush.</summary>
    public void ApplyPendingTransition()
    {
        string? target = _pendingRoomId;
        _pendingRoomId = null;
        if (target is not null)
        {
            LoadRoom(target);
        }
    }

    /// <summary>
    /// GameOver retry (Sprint 2.8): reloads the CURRENT room so its enemies respawn
    /// fresh (Zelda-style). Progression flags are kept as-is — a cleared room stays
    /// cleared. Reuses <see cref="LoadRoom"/>, so the existing guards apply: a failure
    /// (missing scene, stale import, no parent) keeps the current room in place and
    /// returns false. State is NOT touched here — the caller sets Playing after revive.
    /// </summary>
    public bool RetryCurrentRoom()
    {
        if (CurrentRoomId.Length == 0)
        {
            GD.PrintErr("RoomLoader: retry requested with no current room — refused.");
            return false;
        }

        return LoadRoom(CurrentRoomId);
    }

    /// <summary>
    /// Victory continue (Sprint 2.8): loads the hub (first overworld of rooms.json, same
    /// choice as boot) for post-victory roam. Same <see cref="LoadRoom"/> guards — a
    /// failure keeps the current room and returns false. State is NOT touched here.
    /// </summary>
    public bool ReturnToHub()
    {
        string hubRoom = ChooseStartingRoom();
        if (hubRoom.Length == 0)
        {
            GD.PrintErr("RoomLoader: no hub room declared — return-to-hub refused.");
            return false;
        }

        return LoadRoom(hubRoom);
    }

    /// <summary>
    /// Loads <paramref name="roomId"/>'s scene under Main. Validates ResourceLoader.Exists
    /// AND the load result (stale-import null trap) before touching the current room —
    /// a failure leaves the player exactly where they are.
    /// </summary>
    private bool LoadRoom(string roomId)
    {
        RoomRecord? record = null;
        foreach (RoomRecord candidate in GameData.Rooms)
        {
            if (candidate.Id == roomId)
            {
                record = candidate;
                break;
            }
        }

        if (record is null)
        {
            GD.PrintErr($"RoomLoader: rooms.json has no room '{roomId}'.");
            return false;
        }

        string scenePath = LevelsDir + ToPascalCase(roomId) + SceneExtension;
        if (!ResourceLoader.Exists(scenePath))
        {
            GD.PrintErr($"RoomLoader: scene not found '{scenePath}' — current room kept.");
            return false;
        }

        PackedScene? scene = ResourceLoader.Load<PackedScene>(scenePath);
        if (scene is null)
        {
            GD.PrintErr($"RoomLoader: '{scenePath}' returned null (stale import?) — current room kept.");
            return false;
        }

        Node? room = scene.Instantiate();
        if (room is null)
        {
            GD.PrintErr($"RoomLoader: instantiate failed for '{scenePath}' — current room kept.");
            return false;
        }

        Node? parent = GetParent();
        if (parent is null)
        {
            room.QueueFree();
            GD.PrintErr("RoomLoader: no parent to host rooms — load aborted.");
            return false;
        }

        // Every fallible step passed — only now swap: invalidate old callbacks, free, add.
        _generation++;
        _fragmentPickup = null;
        _aliveEnemies = 0;
        _currentRoom?.QueueFree();
        _currentRoom = room;
        parent.AddChild(room);
        Kit.MeshKit.ApplyVertexColors(room); // glTF COLOR_0 needs VertexColorUseAsAlbedo or painted art renders white
        CurrentRoomId = roomId;

        PositionPlayer(room);
        AttachRoomAreasAfterPlayerSync(room, record, _generation); // deferred — see method doc
        WireEnemyTracking(room, record);
        GD.Print($"Room: {record.DisplayName} ({roomId})");
        return true;
    }

    /// <summary>Drops the player on the room's PlayerStart and kills residual drift.</summary>
    private void PositionPlayer(Node room)
    {
        if (_player is null)
        {
            return; // error already logged by ResolvePlayer
        }

        Marker3D? start = room.GetNodeOrNull<Marker3D>(PlayerStartNodeName);
        if (start is null)
        {
            GD.PrintErr($"RoomLoader: '{PlayerStartNodeName}' missing in '{CurrentRoomId}' — player not repositioned.");
            return;
        }

        _player.GlobalPosition = start.GlobalPosition;
        _player.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Sprint 2.6E (phantom-transition fix): attaches the room's transition triggers and
    /// win pickup only after the player's body has actually synced to
    /// <see cref="PositionPlayer"/>'s new transform. The CharacterBody3D's collision
    /// position catches up on a later physics step, so a same-frame Area3D
    /// initial-overlap sweep still sees the OLD room's walk-out position — which lands
    /// inside this room's arch footprint (the dungeon scenes share one z-layout) and
    /// fired phantom <c>BodyEntered</c> swaps (the "Locked: dungeon_02 -&gt; dungeon_03"
    /// line one tick after load). Two physics frames are awaited because the earliest
    /// attach point is only safe once a full step has completed since the position
    /// write, regardless of whether the await continuation runs at signal time or
    /// end-of-frame; the position is re-asserted immediately before attaching as belt
    /// &amp; braces. Generation-guarded so a superseded load never wires a stale room.
    /// Late attach cannot strand anyone: PlayerStart sits outside every footprint, a
    /// player at spawn cannot walk 0.9 m in two ticks, and areas still report bodies
    /// already overlapping when monitoring starts (no lost win pickup).
    /// </summary>
    private async void AttachRoomAreasAfterPlayerSync(Node room, RoomRecord record, ulong generation)
    {
        SceneTree? tree = GetTree();
        if (tree is null)
        {
            GD.PrintErr($"RoomLoader: no SceneTree to defer area attach in '{record.Id}' — attaching immediately.");
            AttachRoomAreas(room, record, generation);
            return;
        }

        await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        AttachRoomAreas(room, record, generation);
    }

    /// <summary>Wires the room's areas after the deferred wait; discards itself when a newer load superseded this one.</summary>
    private void AttachRoomAreas(Node room, RoomRecord record, ulong generation)
    {
        if (generation != _generation || !GodotObject.IsInstanceValid(room))
        {
            return; // superseded by a newer load (or already freed) — its wiring must never attach
        }

        PositionPlayer(room); // belt & braces: exact spawn point re-asserted post-sync
        RoomTransition.AttachAll(room, record, this);
        _fragmentPickup = CrownFragmentPickup.Attach(room, record, this);
    }

    /// <summary>Counts enemy instances; when the last one leaves the tree (freed on death), the room clears.</summary>
    private void WireEnemyTracking(Node room, RoomRecord record)
    {
        Node? container = room.GetNodeOrNull<Node3D>(EnemiesNodeName);
        if (container is null)
        {
            GD.PrintErr($"RoomLoader: no '{EnemiesNodeName}' node in '{record.Id}' — treating as already cleared.");
            MarkRoomCleared(_generation, record);
            return;
        }

        ulong generation = _generation;
        foreach (Node enemy in container.GetChildren())
        {
            _aliveEnemies++;
            enemy.TreeExiting += () => OnEnemyRemoved(generation, record);
        }

        if (_aliveEnemies == 0)
        {
            MarkRoomCleared(generation, record);
        }
    }

    private void OnEnemyRemoved(ulong generation, RoomRecord record)
    {
        if (generation != _generation)
        {
            return; // stale callback from a room that was swapped out — expected, ignore
        }

        _aliveEnemies--;
        if (_aliveEnemies > 0)
        {
            return;
        }

        MarkRoomCleared(generation, record);
    }

    /// <summary>Sets the room's clear flag (unlocking its dependents) and the boss flag in boss rooms.</summary>
    private void MarkRoomCleared(ulong generation, RoomRecord record)
    {
        if (generation != _generation || Progression is null || Progression.IsCleared(record.Id))
        {
            return;
        }

        Progression.MarkCleared(record.Id);
        GD.Print($"Room cleared: {record.Id}");

        IReadOnlyList<string> unlocks = Progression.GetUnlocksForRoom(record.Id);
        if (unlocks.Count > 0)
        {
            GD.Print($"Unlocked: {string.Join(", ", unlocks)}");
        }

        if (record.RoomType == BossRoomType)
        {
            Progression.MarkBossDefeated();
            GD.Print("The Crown Guardian has fallen.");
            ScheduleFragmentRecheck(generation);
        }
    }

    /// <summary>Boss-death pickup edge: re-checks the win area a frame later (player may already stand on it).</summary>
    private void ScheduleFragmentRecheck(ulong generation)
    {
        if (_fragmentPickup is null || generation != _generation)
        {
            return;
        }

        CrownFragmentPickup pickup = _fragmentPickup;
        Callable.From(pickup.RecheckOverlap).CallDeferred();
    }

    /// <summary>Group lookup first, Main's "Player" child as fallback.</summary>
    private CharacterBody3D? ResolvePlayer()
    {
        Node? node = GetTree()?.GetFirstNodeInGroup(PlayerGroup);
        CharacterBody3D? player = node as CharacterBody3D ?? GetParent()?.GetNodeOrNull<CharacterBody3D>(PlayerSiblingName);
        if (player is null)
        {
            GD.PrintErr($"RoomLoader: player not found (group '{PlayerGroup}' / child '{PlayerSiblingName}').");
        }

        return player;
    }

    /// <summary>First overworld room of rooms.json, else the first declared room.</summary>
    private string ChooseStartingRoom()
    {
        foreach (RoomRecord room in GameData.Rooms)
        {
            if (room.RoomType == OverworldRoomType)
            {
                return room.Id;
            }
        }

        return GameData.Rooms.Count > 0 ? GameData.Rooms[0].Id : string.Empty;
    }

    /// <summary>Boot → Playing once the first room is up (GameManager starts in Menu).</summary>
    private static void EnterPlayingState()
    {
        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null)
        {
            return;
        }

        if (gameManager.State == GameManager.GameState.Boot || gameManager.State == GameManager.GameState.Menu)
        {
            gameManager.SetState(GameManager.GameState.Playing);
        }
    }

    /// <summary>Boot telemetry: what enemies.json actually loaded (ids, HP, damage).</summary>
    private static void LogEnemyRoster()
    {
        foreach (EnemyRecord enemy in GameData.Enemies)
        {
            GD.Print($"Enemy roster: {enemy.Name} ({enemy.Id}) — {enemy.Hp} HP, {enemy.Damage} dmg.");
        }
    }

    /// <summary>rooms.json id → scene/node PascalCase ("dungeon_01" → "Dungeon01").</summary>
    public static string ToPascalCase(string id)
    {
        var result = new System.Text.StringBuilder(id.Length);
        foreach (string part in id.Split('_'))
        {
            if (part.Length > 0)
            {
                result.Append(char.ToUpperInvariant(part[0]));
                result.Append(part.AsSpan(1));
            }
        }

        return result.ToString();
    }
}
