using Godot;
using Kit;
using VerdantCrown.Combat;

namespace VerdantCrown.World;

/// <summary>
/// Sprint 2 combat proof: top-down CharacterBody3D controller that moves on the XZ
/// plane, faces its travel direction via the visible placeholder, swings the child
/// sword hitbox on attack, and dodges with a short direction burst on dodge.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
    // --- Movement tuning ---------------------------------------------------
    private const float MoveSpeed = 5.0f;            // metres per second on the XZ plane
    private const float TurnResponsiveness = 12.0f;  // 1/second — smooth facing toward travel direction
    private const float GravityAcceleration = 20.0f; // m/s^2 — settles the body on the whitebox floor
    private const float MaxTurnWeight = 1.0f;        // clamp for the per-frame facing lerp

    // --- Input actions (bound in project.godot) ----------------------------
    private const string MoveLeftAction = "move_left";
    private const string MoveRightAction = "move_right";
    private const string MoveUpAction = "move_up";
    private const string MoveDownAction = "move_down";
    private const string AttackAction = "attack";
    private const string DodgeAction = "dodge";

    // --- Dodge tuning ------------------------------------------------------
    private const float DodgeBurstSpeed = 12.0f;     // metres per second while the burst is live
    private const float DodgeDurationSeconds = 0.15f; // length of the dodge burst
    private const float DodgeCooldownSeconds = 0.7f;  // refractory period after a dodge
    private const float DodgeIFrameMarginSeconds = 0.1f; // i-frame padding past the burst so contact can't clip the tail
    private const float TimerFloor = 0.0f;            // clamp so timers never go negative

    // --- Collision capsule -------------------------------------------------
    // Tuned to movement, bands and gate pads — DO NOT change (must match Player.tscn).
    private const float CapsuleRadiusMeters = 0.3f;
    private const float CapsuleHeightMeters = 1.2f;

    /// <summary>Distance from the capsule centre (the player root origin) down to its bottom face.</summary>
    private const float CapsuleHalfHeightMeters = CapsuleHeightMeters * 0.5f;

    // --- Scene wiring ------------------------------------------------------
    private const string VisualNodePath = "MeshInstance3D";

    /// <summary>Sprint 3c hero rig instanced under the facing node in Player.tscn.</summary>
    private const string HeroNodeName = "Hero";

    /// <summary>
    /// The sword hangs under the ROTATED visual placeholder, not the world-aligned body:
    /// UpdateFacing yaws only the visual, so the swing box always lands where the player
    /// faces (QA finding #7 — it used to swing at world -Z forever).
    /// </summary>
    private const string SwordNodePath = "MeshInstance3D/Sword";

    private MeshInstance3D? _visual;
    private SwordHitbox? _sword;

    /// <summary>
    /// Rising-edge trackers polled in _PhysicsProcess. The in-game test pilot
    /// drives actions via held state only (no InputEvent dispatch), so event
    /// callbacks would miss them. Polling also handles real keyboard input.
    /// </summary>
    private readonly InputEdge _attackEdge = new(AttackAction);
    private readonly InputEdge _dodgeEdge = new(DodgeAction);
    private PlayerHealth? _health;

    /// <summary>
    /// Sprint 3e: procedural walk cycle. Constructed with MoveSpeed as the full-stride
    /// reference speed; limbs resolve once in _Ready, and Tick runs after MoveAndSlide.
    /// </summary>
    private readonly LimbAnimator _limbAnimator = new(MoveSpeed);

    private float _facingYaw;
    private float _dodgeTimeRemaining;
    private float _dodgeCooldownRemaining;
    private Vector3 _dodgeDirection = Vector3.Back;

    public override void _Ready()
    {
        _visual = GetNodeOrNull<MeshInstance3D>(VisualNodePath);
        if (_visual is null)
        {
            GD.PrintErr($"{Name}: PlayerController could not find '{VisualNodePath}' — " +
                        "facing rotation will be applied to the body instead.");
        }

        _sword = GetNodeOrNull<SwordHitbox>(SwordNodePath);
        if (_sword is null)
        {
            GD.PrintErr($"{Name}: PlayerController could not find '{SwordNodePath}' — " +
                        "the attack action will do nothing.");
        }

        _health = GetNodeOrNull<PlayerHealth>("Health");
        if (_health is null)
        {
            GD.PrintErr($"{Name}: PlayerController could not find the 'Health' node — " +
                        "dodges will not grant i-frames.");
        }

        AttachHeroVisual();

        // Sprint 3e: one recursive NAME search under the facing node (falls back to the
        // body root if the visual is missing) — never resolved again in the hot path.
        _limbAnimator.Resolve(_visual ?? (Node3D)this);
    }

    /// <summary>
    /// Sprint 3c: pins the hero.glb instance feet-on-floor under the facing node.
    /// Offset math (same pattern as EnemyBrain's box models): the player root origin sits at
    /// the capsule CENTRE — levels drop the body so the capsule bottom face (local y =
    /// −height/2) rests on the floor with the root at y = 0.6 — while hero.glb is authored
    /// origin-at-feet, so the hero child sits at local y = −CapsuleHalfHeightMeters to put
    /// its feet on the floor. Player.tscn carries the same −0.6 baked in; re-applying it here
    /// keeps the maths derived from one named constant instead of a magic number.
    /// </summary>
    private void AttachHeroVisual()
    {
        Node3D? hero = _visual?.GetNodeOrNull<Node3D>(HeroNodeName);
        if (hero is null)
        {
            GD.PrintErr($"{Name}: hero instance '{HeroNodeName}' missing under '{VisualNodePath}' — " +
                        "the player will render without a body.");
            return;
        }

        Kit.MeshKit.ApplyVertexColors(hero); // painted tunic/skin/steel need the albedo flag
        hero.Position = new Vector3(0f, -CapsuleHalfHeightMeters, 0f); // feet on the floor
    }

    /// <summary>RAW input observer (#23): logs the physical event BEFORE action mapping, so a
    /// session log answers "what did my fingers do" next to the derived [edge] lines. Joystick
    /// axes only log when they cross the deadzone boundary (a held stick must not flood).</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventJoypadButton jb)
        {
            GD.Print($"[raw] joybtn{jb.ButtonIndex} {(jb.Pressed ? "down" : "up")} dev={jb.Device}");
        }
        else if (@event is InputEventKey k && !k.Echo)
        {
            Key code = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
            GD.Print($"[raw] key {code} {(k.Pressed ? "down" : "up")} dev={k.Device}");
        }
        else if (@event is InputEventJoypadMotion m)
        {
            int i = (int)m.Axis;
            bool engaged = Mathf.Abs(m.AxisValue) > 0.5f;
            if (i >= 0 && i < _rawAxisEngaged.Length && engaged != _rawAxisEngaged[i])
            {
                _rawAxisEngaged[i] = engaged;
                GD.Print($"[raw] axis{i} {(engaged ? "ENGAGED" : "released")} v={m.AxisValue:0.00}");
            }
        }
    }

    private readonly bool[] _rawAxisEngaged = new bool[8];

    public override void _PhysicsProcess(double delta)
    {
        // Evaluate input edges first: one rising edge per press/hold, regardless of
        // whether the press came from a human key event or held-state injection.
        _attackEdge.Update();
        _dodgeEdge.Update();
        if (_attackEdge.Rising || _dodgeEdge.Rising)
        {
            // #23 probe: one line PER PRESS with BOTH edges — settles single-press-does-both
            // (atk=1 dod=1 on one line = real double-fire; separate lines = two distinct presses).
            GD.Print($"[edge] atk={(_attackEdge.Rising ? 1 : 0)} dod={(_dodgeEdge.Rising ? 1 : 0)}");
        }
        // Dodge FIRST so a same-tick A+B chord sees the burst it just started — otherwise the
        // attack slipped through the gate on the chord frame (the human chord test found this hole).
        if (_dodgeEdge.Rising)
        {
            TryDodge();
        }

        bool isDodging = _dodgeTimeRemaining > TimerFloor;
        if (_attackEdge.Rising && !isDodging)
        {
            _sword?.Swing(); // ignored by the sword while its cooldown is still running
        }
        else if (_attackEdge.Rising)
        {
            // Effect-level proof (#23): input edges cannot show this gate — the line can.
            GD.Print("[fx] swing BLOCKED by dodge");
        }

        // move_up maps to -Z (away from the camera), move_down to +Z.
        Vector2 input = Input.GetVector(MoveLeftAction, MoveRightAction, MoveUpAction, MoveDownAction);
        Vector3 direction = new Vector3(input.X, 0f, input.Y).LimitLength(1f);

        _dodgeCooldownRemaining = Mathf.Max(TimerFloor, _dodgeCooldownRemaining - (float)delta);
        // isDodging was computed above (before the edge handlers) — see #23.
        if (isDodging)
        {
            _dodgeTimeRemaining = Mathf.Max(TimerFloor, _dodgeTimeRemaining - (float)delta);
            UpdateFacing(_dodgeDirection, delta);
        }
        else
        {
            UpdateFacing(direction, delta);
        }

        Vector3 velocity = Velocity;
        if (!IsOnFloor())
        {
            velocity.Y -= (float)(GravityAcceleration * delta);
        }

        velocity.X = isDodging ? _dodgeDirection.X * DodgeBurstSpeed : direction.X * MoveSpeed;
        velocity.Z = isDodging ? _dodgeDirection.Z * DodgeBurstSpeed : direction.Z * MoveSpeed;
        Velocity = velocity;

        MoveAndSlide();

        // Sprint 3e: feed the animator AFTER the slide — position delta is the odometer
        // (truth), plus this tick's freeze flags. Only the animator writes limb joints.
        _limbAnimator.Tick(delta, GlobalPosition, isDodging, _sword?.IsActive ?? false);
    }

    /// <summary>
    /// Starts a dodge burst in the current input direction — or straight ahead along
    /// the current facing when idle — if the dodge cooldown has elapsed.
    /// </summary>
    private void TryDodge()
    {
        if (_dodgeCooldownRemaining > TimerFloor)
        {
            GD.Print("[fx] dodge ignored (cooldown)");
            return;
        }

        GD.Print("[fx] dodge BURST start");

        Vector2 input = Input.GetVector(MoveLeftAction, MoveRightAction, MoveUpAction, MoveDownAction);
        Vector3 direction = new Vector3(input.X, 0f, input.Y).LimitLength(1f);
        if (direction.IsZeroApprox())
        {
            // Forward is -Z yawed by the current facing angle.
            direction = new Vector3(-Mathf.Sin(_facingYaw), 0f, -Mathf.Cos(_facingYaw));
        }

        _dodgeDirection = direction;
        _dodgeTimeRemaining = DodgeDurationSeconds;
        _dodgeCooldownRemaining = DodgeCooldownSeconds;

        // Dodge is the escape tool against out-reaching bosses: make the burst
        // invincible for its duration plus a small margin (≈0.25s total).
        _health?.GrantInvulnerability(DodgeDurationSeconds + DodgeIFrameMarginSeconds);
    }

    /// <summary>
    /// Yaws the visible placeholder toward the travel direction. The body itself
    /// stays world-aligned so the child Camera3D never swings with player facing.
    /// Falls back to rotating the body when the visual node is missing.
    /// </summary>
    private void UpdateFacing(Vector3 direction, double delta)
    {
        if (direction.IsZeroApprox())
        {
            return;
        }

        // Forward is -Z: yaw such that (0,0,-1) rotated by yaw equals direction.
        float targetYaw = (float)Mathf.Atan2(-direction.X, -direction.Z);
        double weight = Mathf.Min(TurnResponsiveness * delta, MaxTurnWeight);
        _facingYaw = (float)Mathf.LerpAngle(_facingYaw, targetYaw, weight);

        Node3D facingTarget = _visual ?? (Node3D)this;
        facingTarget.Rotation = new Vector3(0f, _facingYaw, 0f);
    }
}
