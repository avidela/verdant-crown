using Godot;
using Kit;
using VerdantCrown.Combat.AI;
using VerdantCrown.Core;
using VerdantCrown.Data;
using VerdantCrown.World;

namespace VerdantCrown.Combat;

/// <summary>
/// Data-bound enemy root of EnemyBody.tscn (CharacterBody3D, layer 2 / mask 1) — replaces
/// the static EnemyDummy wiring for level enemies (QA finding #3). Binds hp, damage,
/// speed_mps, aggro_range_m, behavior and phases from enemies.json via
/// <see cref="GameData"/> (no balance numbers duplicated as constants), implements
/// <see cref="IDamageable"/> with real hit points, and drives a Kit FSM —
/// Idle/Wander → Chase → Attack — with contact damage debounced by
/// <see cref="PlayerHealth.InvulnWindowSeconds"/>. Boss phase tables are delegated to
/// <see cref="EnemyPhaseDirector"/>; reinforcements to <see cref="PhaseReinforcementSpawner"/>.
/// <para>The <see cref="EnemyId"/> export picks the roster entry (prefab default
/// <see cref="DefaultEnemyId"/>; unknown ids fall back to entry 0 with an error).</para>
/// <para>Sprint 3a (art integration): on bind, the grey-box BoxMesh visual slot is swapped for
/// the data-mapped GLB model (<c>res://Assets/Models/glb/&lt;id&gt;.glb</c>) instanced under the
/// slot, feet dropped to the collision box's bottom face. The swap only happens on a fully
/// successful load — any failure keeps the box fallback and the fight keeps working.</para>
/// </summary>
public partial class EnemyBrain : CharacterBody3D, IDamageable
{
    // --- Kit.StateMachine state names --------------------------------------
    public const string StateIdle = "Idle";
    public const string StateChase = "Chase";
    public const string StateAttack = "Attack";

    // --- Data / scene conventions ------------------------------------------
    private const string DefaultEnemyId = "slime"; // prefab default; scene instances override EnemyId
    private const string CollisionShapePath = "CollisionShape3D";
    private const string PlayerHealthChildPath = "Health"; // Player.tscn child of group "player"
    private const string VisualSlotPath = "MeshInstance3D"; // EnemyBody.tscn grey-box mesh = visual slot

    // --- Model art convention (Sprint 3a) ---------------------------------
    private const string ModelDirectory = "res://Assets/Models/glb/";
    private const string BossEnemyId = "boss_guardian";   // enemies.json id
    private const string BossModelFileName = "crown_guardian.glb"; // art file name differs from the roster id
    private const float BoxHalfHeightFallbackMeters = 0.5f; // prefab BoxShape3D is 1×1×1

    /// <summary>
    /// Maps an enemies.json id to its model file: <c>slime→slime.glb</c>, <c>skeleton→skeleton.glb</c>,
    /// <c>boss_guardian→crown_guardian.glb</c> (the boss art is named differently); any other id
    /// resolves to <c>&lt;id&gt;.glb</c> and is guarded by <see cref="ResourceLoader.Exists"/> at the call site.
    /// </summary>
    private static string ModelFileNameFor(string enemyId) =>
        enemyId == BossEnemyId ? BossModelFileName : $"{enemyId}.glb";

    // --- Mechanics tuning (not balance — roster data always wins) -----------
    private const float GravityAcceleration = 20.0f;   // m/s^2 — mirrors PlayerController
    private const float ChaseExitHysteresis = 1.2f;    // leave Chase beyond aggro × this
    private const float PlayerBodyRadiusMeters = 0.3f; // Player.tscn capsule radius
    private const float ContactPaddingMeters = 0.1f;   // margin so touching counts as in range
    private const float ContactRangeFallbackMeters = 0.9f; // half box 0.5 + radius 0.3 + pad 0.1
    private const float HitPulseScale = 1.15f;         // whitebox hit feedback (as EnemyDummy)
    private const float HitPulseSeconds = 0.1f;

    /// <summary>Only fallback when enemies.json failed to load entirely — killable, but inert.</summary>
    private const int FallbackMaxHp = 3;

    /// <summary>enemies.json id this instance fights as ("slime", "skeleton", "boss_guardian").</summary>
    [Export]
    public string EnemyId { get; set; } = DefaultEnemyId;

    // --- Bound data ---------------------------------------------------------
    private EnemyRecord? _record;
    private EnemyPhaseDirector? _phases;
    private int _hp;
    private int _maxHp;
    private int _contactDamage;
    private float _contactRange = ContactRangeFallbackMeters;

    // --- Runtime ------------------------------------------------------------
    private StateMachine<EnemyBrain>? _machine;
    private Node3D? _playerNode;
    private PlayerHealth? _playerHealth;
    private Tween? _pulseTween;
    private Vector3 _restScale = Vector3.One;
    private bool _dead;

    /// <summary>Spawn position — wander anchor for the Idle state.</summary>
    public Vector3 HomePosition { get; private set; }

    /// <summary>Aggro radius in metres (enemies.json aggro_range_m).</summary>
    public float AggroRangeMeters { get; private set; }

    /// <summary>Current ground speed in m/s — record speed, overridden by the active boss phase.</summary>
    public float CurrentSpeedMps { get; private set; }

    /// <summary>Bound behavior tag ("wander", "charge_player", "boss_swing"); phase-swappable.</summary>
    public string Behavior { get; private set; } = string.Empty;

    /// <summary>Hit points remaining; never below zero.</summary>
    public int Hp => _hp;

    /// <summary>Distance at which Chase gives up (aggro × hysteresis) — prevents boundary flicker.</summary>
    public float ChaseExitRange => AggroRangeMeters * ChaseExitHysteresis;

    /// <summary>Centre-to-centre distance at which contact damage lands (collision box + scale derived).</summary>
    public float ContactRange => _contactRange;

    /// <summary>Player root position (group <see cref="RoomLoader.PlayerGroup"/>), or null when absent.</summary>
    public Vector3? PlayerPosition => ResolvePlayer()?.GlobalPosition;

    public override void _Ready()
    {
        GameData.EnsureLoaded();
        HomePosition = GlobalPosition;
        _restScale = Scale; // before CaptureContactRange: contact range scales with the instance
        BindFromData();
        TryAttachModelVisual(); // Sprint 3a: grey-box box → real GLB (box kept as fallback on failure)
        CaptureContactRange();

        _machine = new StateMachine<EnemyBrain>(this);
        _machine.Add(StateIdle, new IdleWanderState());
        _machine.Add(StateChase, new ChaseState());
        _machine.Add(StateAttack, new AttackState());
        _machine.Move(StateIdle);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_dead)
        {
            return;
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is not null && gameManager.State != GameManager.GameState.Playing)
        {
            Halt((float)delta); // menu / pause / Victory / GameOver: settle, take no actions
            return;
        }

        _machine?.Update((float)delta);
    }

    /// <summary>Applies damage, advances boss phases, pulses, and frees the body at zero HP.</summary>
    public void TakeDamage(int amount)
    {
        if (_dead || amount <= 0)
        {
            return;
        }

        _hp = Mathf.Max(0, _hp - amount);
        GD.Print($"{Name}: took {amount} damage ({_hp}/{_maxHp} HP).");

        if (_hp <= 0)
        {
            Die();
            return;
        }

        _phases?.Evaluate(_hp);
        Pulse();
    }

    /// <summary>FSM transition requested by the active state; Kit ignores unknown names.</summary>
    public void TransitionTo(string stateName) => _machine?.Move(stateName);

    /// <summary>Horizontal (XZ) distance to a player position.</summary>
    public float DistanceToPlayer(Vector3 playerPosition)
    {
        Vector3 delta = GlobalPosition - playerPosition;
        delta.Y = 0f;
        return delta.Length();
    }

    /// <summary>Walks toward <paramref name="target"/> on the XZ plane at <see cref="CurrentSpeedMps"/>, sliding along obstacles.</summary>
    public void MoveToward(Vector3 target, float delta)
    {
        Vector3 toTarget = target - GlobalPosition;
        toTarget.Y = 0f;
        Vector3 direction = toTarget.LengthSquared() > 0f ? toTarget.Normalized() : Vector3.Zero;
        MoveHorizontal(direction * CurrentSpeedMps, delta);
    }

    /// <summary>Stops on the XZ plane but keeps gravity + sliding (Idle pauses, menu gate).</summary>
    public void Halt(float delta) => MoveHorizontal(Vector3.Zero, delta);

    /// <summary>
    /// Deals the record's damage when the player is inside <see cref="ContactRange"/>.
    /// Called every tick while touching — <see cref="PlayerHealth.InvulnWindowSeconds"/>
    /// turns that into one hit per window.
    /// </summary>
    public void TryDealContactDamage()
    {
        Vector3? playerPosition = PlayerPosition;
        if (playerPosition is null || _contactDamage <= 0
            || DistanceToPlayer(playerPosition.Value) > _contactRange)
        {
            return;
        }

        ResolvePlayerHealth()?.TakeDamage(_contactDamage, Name);
    }

    /// <summary>Phase hook: swap the active speed (non-positive values are ignored).</summary>
    public void SetPhaseSpeed(float speedMps)
    {
        if (speedMps > 0f)
        {
            CurrentSpeedMps = speedMps;
        }
    }

    /// <summary>Phase hook: swap the active behavior tag (empty values are ignored).</summary>
    public void SetPhaseBehavior(string behavior)
    {
        if (!string.IsNullOrEmpty(behavior))
        {
            Behavior = behavior;
        }
    }

    /// <summary>Binds this instance to its enemies.json roster entry; unknown ids fall back to entry 0.</summary>
    private void BindFromData()
    {
        string id = string.IsNullOrEmpty(EnemyId) ? DefaultEnemyId : EnemyId;
        foreach (EnemyRecord candidate in GameData.Enemies)
        {
            if (candidate.Id == id)
            {
                _record = candidate;
                break;
            }
        }

        if (_record is null)
        {
            if (GameData.Enemies.Count == 0)
            {
                GD.PushError($"{Name}: enemies.json roster empty — enemy inert with fallback HP {FallbackMaxHp}.");
                _hp = _maxHp = FallbackMaxHp;
                return;
            }

            _record = GameData.Enemies[0];
            GD.PushError($"{Name}: enemies.json has no '{id}' — falling back to roster entry '{_record.Id}'.");
        }

        _hp = _maxHp = _record.Hp > 0 ? _record.Hp : FallbackMaxHp;
        _contactDamage = _record.Damage;
        CurrentSpeedMps = _record.SpeedMps;
        AggroRangeMeters = _record.AggroRangeM;
        Behavior = _record.Behavior;

        GD.Print($"{Name}: bound '{_record.Name}' — {_hp} HP, {_contactDamage} dmg, " +
                 $"{CurrentSpeedMps} m/s, aggro {AggroRangeMeters} m, '{Behavior}'.");

        _phases = new EnemyPhaseDirector(this, _record, _maxHp);
        _phases.Evaluate(_hp); // boot into phase 1 (no threshold is hardcoded)
    }

    /// <summary>
    /// Sprint 3a: instances the id-mapped GLB under the grey-box visual slot and clears the box
    /// mesh — but only after every load stage succeeded. <see cref="ResourceLoader.Exists"/> plus a
    /// null check on <see cref="ResourceLoader.Load{T}(string)"/> cover the stale-import trap (Exists
    /// true while load returns null), and the boss GLB may be absent mid-sprint; every failure path
    /// keeps the existing BoxMesh child so the fight renders with the fallback.
    /// <para>Offset math: the enemy root origin sits at the collision-box centre (levels place the
    /// 1×1×1 box so its bottom face rests on the floor at local y = −0.5), while every model is
    /// authored feet-at-origin → child y = −box.Size.Y / 2 puts the feet on the box's bottom face.
    /// The child scales with the instance, so Dungeon05's ×2 boss keeps feet on the floor.
    /// Models face −Z like CharacterBody3D forward → no yaw; authored size kept (slime 0.7 m in a
    /// 1 m box is intentionally smaller, skeleton 1.8 m overhangs the 1 m box top).</para>
    /// </summary>
    private void TryAttachModelVisual()
    {
        string id = _record?.Id ?? (string.IsNullOrEmpty(EnemyId) ? DefaultEnemyId : EnemyId);
        string path = ModelDirectory + ModelFileNameFor(id);

        if (!ResourceLoader.Exists(path))
        {
            GD.Print($"{Name}: model '{path}' not found — grey-box visual kept.");
            return;
        }

        PackedScene? modelScene = ResourceLoader.Load<PackedScene>(path);
        if (modelScene is null)
        {
            GD.PrintErr($"{Name}: model '{path}' exists but loaded null (stale import?) — grey-box visual kept.");
            return;
        }

        Node? instantiated = modelScene.Instantiate();
        if (instantiated is not Node3D model)
        {
            instantiated?.Free();
            GD.PrintErr($"{Name}: model '{path}' did not instantiate a Node3D root — grey-box visual kept.");
            return;
        }

        MeshInstance3D? slot = GetNodeOrNull<MeshInstance3D>(VisualSlotPath);
        if (slot is null)
        {
            model.Free();
            GD.PrintErr($"{Name}: visual slot '{VisualSlotPath}' missing — grey-box visual kept.");
            return;
        }

        float halfBoxHeight = BoxHalfHeightFallbackMeters;
        if (GetNodeOrNull<CollisionShape3D>(CollisionShapePath)?.Shape is BoxShape3D box)
        {
            halfBoxHeight = box.Size.Y * 0.5f;
        }

        model.Position = new Vector3(0f, -halfBoxHeight, 0f); // feet on the box's bottom face
        slot.AddChild(model);
        slot.Mesh = null; // swap only now — box mesh untouched on every failure path above
        GD.Print($"{Name}: visual ← {path} (feet at local y = {-halfBoxHeight:F2}).");
    }

    /// <summary>Contact geometry: widest horizontal box edge / 2 + player radius + padding, scaled with the instance.</summary>
    private void CaptureContactRange()
    {
        float range = ContactRangeFallbackMeters;
        if (GetNodeOrNull<CollisionShape3D>(CollisionShapePath)?.Shape is BoxShape3D box)
        {
            range = Mathf.Max(box.Size.X, box.Size.Z) * 0.5f
                + PlayerBodyRadiusMeters + ContactPaddingMeters;
        }

        _contactRange = range * Mathf.Max(Mathf.Abs(_restScale.X), Mathf.Abs(_restScale.Z));
    }

    private void MoveHorizontal(Vector3 horizontal, float delta)
    {
        Vector3 velocity = Velocity;
        if (!IsOnFloor())
        {
            velocity.Y -= (float)(GravityAcceleration * delta);
        }

        velocity.X = horizontal.X;
        velocity.Z = horizontal.Z;
        Velocity = velocity;
        MoveAndSlide();
    }

    private Node3D? ResolvePlayer()
    {
        if (GodotObject.IsInstanceValid(_playerNode))
        {
            return _playerNode;
        }

        _playerNode = GetTree()?.GetFirstNodeInGroup(RoomLoader.PlayerGroup) as Node3D;
        return _playerNode;
    }

    private PlayerHealth? ResolvePlayerHealth()
    {
        if (GodotObject.IsInstanceValid(_playerHealth))
        {
            return _playerHealth;
        }

        _playerHealth = ResolvePlayer()?.GetNodeOrNull<PlayerHealth>(PlayerHealthChildPath);
        return _playerHealth;
    }

    private void Die()
    {
        _dead = true;
        _pulseTween?.Kill();
        string roster = _record is null ? Name : $"{_record.Name} ({_record.Id})";
        GD.Print($"{Name}: {roster} defeated.");
        QueueFree(); // RoomLoader sees the tree exit and counts the room toward clearing
    }

    private void Pulse()
    {
        _pulseTween?.Kill();
        Scale = _restScale * HitPulseScale;
        _pulseTween = CreateTween();
        _pulseTween.TweenProperty(this, "scale", _restScale, HitPulseSeconds)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
    }
}
