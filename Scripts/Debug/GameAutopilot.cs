using Godot;
using VerdantCrown.Combat;
using VerdantCrown.Core;
using VerdantCrown.Data;
using VerdantCrown.World;

namespace VerdantCrown.Debug;

/// <summary>
/// In-game test pilot (Sprint 2.6B, extended 2.6D "GameAutopilot v2", Sprint 2.6F "GameAutopilot v3",
/// Sprint 2.6H sticky target lock): plays the
/// real-time game at frame rate through the exact input path a human uses — it ONLY
/// calls <c>Input.ActionPress</c> / <c>Input.ActionRelease</c> on the existing actions
/// (never position writes, teleports or PlayerController/combat calls), so a victory is
/// constructive proof the build is winnable through input alone. If it cannot win
/// through input, it fails loudly — that is the point of the test.
/// <para>Declaration-driven: every target comes from the loaded data/scene — live
/// <c>Enemies</c> children, the runtime-attached <c>Transition_&lt;roomId&gt;</c>
/// triggers (RoomTransition's own naming, chosen from rooms.json exits minus the room
/// the pilot came from), the win-condition fragment node, and the room's declared
/// <c>heart</c> item node — never hand-written waypoints. Off by default:
/// <see cref="PilotEnabled"/> starts false, toggles on the rising edge of the
/// <c>debug_autopilot</c> action (physical F2), via <c>VC_PILOT=1</c> at boot,
/// or through the exported property.</para>
/// <para>FSM (SCAN → HEART / ENGAGE / SEPARATE / EXIT / FRAGMENT) runs in <c>_PhysicsProcess</c>
/// gated to <c>GameManager.State == Playing</c>, with a damage-edge <b>escape
/// overlay</b> that pre-empts the FSM for half a second on every HP drop: release the
/// move actions, re-aim away from the threat-ranked nearest enemy (chargers first,
/// Sprint 2.6F) each tick and pulse <c>dodge</c>
/// (dodge i-frames come from PlayerController), then resume exactly where it left off.
/// ENGAGE fights a data-driven in-and-out strike cycle: approach with the facing
/// settling on the target, stop at the contact-aware band (never walking into the
/// body), swing once per sword cooldown from outside contact, then give ground until
/// the next run-up — against an enemy that is weave-mode (contact beyond
/// <see cref="AttackReachLimit"/>, or charger-tier behavior per
/// <see cref="IsWeaveTarget"/>, Sprint 2.6G) it logs <c>status=weave</c> and layers
/// dodge bursts into the cycle so every swing lands while i-framed. Below 3 HP, a room
/// that declares the <c>heart</c> item and still has its live <c>heart</c> node walks
/// that pickup first (<c>status=heart</c>); Sprint 2.6F opens the same detour mid-fight:
/// a damage edge landing at or below 2 HP during ENGAGE plans HEART through the same
/// v2 checks (no declared heart, or its node already gone ⇒ keep fighting, never
/// fatal) and resumes ENGAGE with a fresh target acquire once the pickup is done.
/// Targets are threat-ranked (Sprint 2.6F): charger-tier <c>Behavior</c> names first,
/// then distance within the tier — so the escape overlay re-aims away from the nearest
/// charger first instead of whatever wanderer happens to be closest. The ranking is
/// evaluated only on SCAN's initial pick, on ENGAGE acquire/re-acquire, and per tick
/// by the movement-only escape re-aim; ENGAGE itself holds a <b>sticky lock</b>
/// (Sprint 2.6H) on its committed target and never re-ranks while that commitment
/// stays valid, so crossing chargers can no longer flip <c>target=</c> every tick and
/// restart the strike cycle before a single swing lands.</para>
/// <para>
/// Sprint 2.6J adds the <b>SEPARATE</b> overlay state (the pincer breaker): the weave is a
/// perfect 1v1, but two chargers converge — the pilot weaves the committed target T while the
/// SECOND charger parks at contact and pumps damage — and away-from-A is toward-B, so the
/// escape alone cannot win it. The human counterplay is SEPARATION (the player's 5.0 m/s
/// outruns a charger's 3.5 m/s): while ENGAGE holds a committed target T, any OTHER live
/// charger-tier enemy inside its OWN data-driven aggro range of the player
/// (<see cref="EnemyBrain.AggroRangeMeters"/>, enemies.json <c>aggro_range_m</c>) diverts the
/// pilot to <c>status=separate</c>, which holds a trigger-gated retreat away from the
/// CENTROID of the WHOLE live charger swarm — committed T INCLUDED (Sprint 2.6K: T chases at
/// charger speed like any other arm, so the earlier others-only aim excluded T and walked the
/// pilot straight INTO T's contact box — fight1's four hits from T while "separating" from
/// the second arm) — while the hold/exit ladder still judges ONLY the other chargers: the
/// retreat ends once every one of THEM is beyond its own aggro + 1.0 m hysteresis, then
/// logs the engage resume line and rejoins the weave on the SAME still-committed target: the
/// sticky lock is never zeroed on the way in or out. A hard <c>SeparateMaxSeconds</c> cap
/// makes a cornered retreat resume the fight anyway with a loud
/// <c>pilot: separate_timeout</c> — separation is a maneuver, never a fail state — and
/// Sprint 2.6K bounds the resume/re-aggro flip-flop at <c>SeparateMaxAttempts</c> bouts per
/// commitment (then a one-shot <c>pilot: separate_gaveup</c> and the pincer is fought as-is)
/// plus a pin check: a retreat vector zeroed by the trigger gate for longer than
/// <c>SeparatePinnedSeconds</c> prints <c>pilot: separate_pinned</c> and takes the resume
/// path early instead of standing pinned in the entry corner. Damage edges during SEPARATE
/// never open the escape overlay (SEPARATE already steers away-from-the-swarm-centroid),
/// and the hp≤2 heart detour still opens from here exactly as from ENGAGE.
/// </para>
/// <para>Failures disable the pilot after one <c>pilot:</c> line: a room that does not
/// swap after the exit trigger was walked through prints <c>FATAL advance_missed</c>,
/// a room swap observed in any state other than EXIT prints
/// <c>FATAL unexpected_swap from=… to=…</c> (Sprint 2.6E), death prints
/// <c>status=died</c>, an unreachable heart or win fragment print their
/// own FATAL — never silent. Sprint 2.6E also gates movement: every retreat/escape
/// vector and the ENGAGE close approach are tested against the current room's
/// <c>Transition_*</c> trigger footprints first — outside EXIT a move that would
/// cross an arch slides along the footprint edge (or stops) instead, so a strike-cycle
/// retreat can no longer back the pilot through an always-open entry arch mid-fight,
/// and dodge pulses are only armed when the whole burst path stays clear.</para>
/// </summary>
public partial class GameAutopilot : Node
{
    /// <summary>
    /// Master switch — false by default (Sprint 2.6B requirement). Flip it with the
    /// rising edge of <c>debug_autopilot</c> (physical F2), through the exported
    /// property, or with VC_PILOT=1 at boot. Fatal/victory outcomes disable it.
    /// </summary>
    [Export]
    public bool PilotEnabled { get; set; }

    // --- Input actions (project.godot [input]) — the pilot's ONLY actuation path ---
    private const string MoveUpAction = "move_up";       // maps to -Z
    private const string MoveDownAction = "move_down";   // maps to +Z
    private const string MoveLeftAction = "move_left";   // maps to -X
    private const string MoveRightAction = "move_right"; // maps to +X
    private const string AttackAction = "attack";
    private const string DodgeAction = "dodge"; // escape overlay + weave only
    private const string DebugToggleAction = "debug_autopilot"; // F2

    // --- Scene / data conventions (names come from the scenes and RoomLoader) ----
    private const string RoomLoaderNodeName = "RoomLoader"; // sibling under Main
    private const string PlayerSiblingName = "Player";      // Main's persistent player child (RoomLoader fallback)
    private const string HealthChildName = "Health";        // Player.tscn child carrying PlayerHealth
    private const string EnemiesNodeName = "Enemies";       // room container (RoomLoader convention)
    private const string TransitionNodePrefix = "Transition_"; // RoomTransition.AttachAll node naming

    // --- Heart detour (rooms.json rooms[].Items, literal node ids) --------------
    /// <summary>rooms.json declares the pickup as <c>{"id": "heart"}</c> and the scene node carries that literal name (NOT PascalCased).</summary>
    private const string HeartItemId = "heart";

    /// <summary>HeartPickup rides a child node of the heart body under this name (scene convention).</summary>
    private const string HeartPickupChildName = "Pickup";

    /// <summary>Detour only at or below this HP (Sprint 2.6D); skipped silently at full health.</summary>
    private const int HeartHpThreshold = 3;

    /// <summary>
    /// Mid-fight detour edge (Sprint 2.6F): ENGAGE opens HEART only on a damage edge
    /// that lands at or below this HP — one below the scan threshold, so a trade that
    /// leaves 3 HP keeps fighting and only a real crisis detours.
    /// </summary>
    private const int MidFightHeartHpThreshold = 2;

    private const float HeartWalkTimeoutSeconds = 10f; // walking the heart must not hang forever either

    // --- Contact-aware band / swing geometry -----------------------------------
    /// <summary>
    /// Minimum standoff beyond the target's actual contact distance
    /// (<see cref="EnemyBrain.ContactRange"/>): the strike cycle stops at or outside
    /// <c>contact + 0.15 m</c> instead of walking into the body (v1 took a hit per
    /// slime mostly during approach). Also the boss weave's landing band.
    /// </summary>
    private const float BandClearance = 0.15f;

    /// <summary>
    /// Centre distance beyond which a normal (contact ≤ this) enemy swing may not
    /// start. The sword box spans 0.3–1.1 m ahead of the player centre, so against a
    /// scale-1 body the swing still connects from here; enemies whose contact exceeds
    /// this (boss, 1.8 m) switch the engage into weave mode with a body-derived reach.
    /// Sprint 2.6G: charger-tier targets (<see cref="ChargerTierBehaviors"/>) also weave
    /// even though their contact (0.9 m) sits below this cap — the walk band traded a
    /// hit per charger kill.
    /// </summary>
    private const float AttackReachLimit = 1.4f;

    /// <summary>Sword box: nearest edge (m ahead of the player centre) a swing can connect at.</summary>
    private const float SwordNearReach = 0.3f;

    /// <summary>Sword box: far tip (m ahead of the player centre) a swing can connect at.</summary>
    private const float SwordTipReach = 1.1f;

    /// <summary>Pull the weave's far/near swing gates in by this much so edge-of-box touches aren't relied on.</summary>
    private const float SwingSafetyMargin = 0.1f;

    // --- Dodge mirrors (PlayerController tuning — read, not called) -------------
    /// <summary>Mirror of PlayerController.DodgeCooldownSeconds — the pilot tracks its own refractory timer because the controller does not expose one.</summary>
    private const float DodgeCooldownSeconds = 0.7f;

    /// <summary>Mirror of PlayerController.DodgeDurationSeconds — how long one burst travels.</summary>
    private const float DodgeBurstSeconds = 0.15f;

    /// <summary>Mirror of PlayerController.DodgeBurstSpeed — burst travel = speed × duration = 1.8 m.</summary>
    private const float DodgeBurstSpeedMps = 12f;

    private const int DodgeHoldTicks = 2; // ActionPress window before release (~2 frames), same shape as the attack pulse

    /// <summary>Full burst travel (m) — the dodge gate arms a pulse only when this whole path stays clear of every trigger footprint.</summary>
    private const float DodgeBurstDistance = DodgeBurstSpeedMps * DodgeBurstSeconds;

    // --- Strike-cycle tuning ----------------------------------------------------
    /// <summary>player.json move_speed_mps (PlayerController.MoveSpeed is authoritative) — step budget math only.</summary>
    private const float FallbackMoveSpeedMps = 5.0f;

    private const float AssumedPhysicsHz = 60f; // default physics tick rate

    /// <summary>
    /// Consecutive toward-steers required before a swing may start. UpdateFacing yaws
    /// at 12/s (0.2 of the remaining angle per tick), so after a full retreat (facing
    /// 180° away) this many ticks bring the facing to ≈26° — inside the sword box's
    /// overlap tolerance at every swing distance — before the pilot commits a press.
    /// </summary>
    private const int FaceSettleTicks = 9;

    /// <summary>Extra metres past the run-up maths before giving ground, so jitter can't cut the re-approach short.</summary>
    private const float RetreatPadding = 0.15f;

    private const float BackTimeoutSeconds = 1.0f; // wall-blocked retreat falls forward into the next run-up instead of hanging

    private const int AttackHoldTicks = 2;               // press window before release (~2 frames)
    private const float AttackCooldownSeconds = 0.4f;    // release window ≈ SwordHitbox's 0.4 s cooldown

    // --- Escape overlay ---------------------------------------------------------
    /// <summary>How long the damage-edge escape keeps re-aiming away and pulsing dodge before the FSM resumes.</summary>
    private const float EscapeSeconds = 0.5f;

    private const float MoveAxisDeadzone = 0.05f; // m per axis — 8-way steering converges inside this

    // --- Pincer separation overlay (Sprint 2.6J) --------------------------------
    /// <summary>
    /// Hysteresis (m) past each trailing charger's OWN aggro range
    /// (<see cref="EnemyBrain.AggroRangeMeters"/>, enemies.json <c>aggro_range_m</c> — 8.0 m
    /// for the skeleton charger) before that charger counts as shaken off: SEPARATE triggers
    /// inside aggro, holds the retreat while any other charger is within aggro + this, and
    /// resumes the weave only once ALL others are beyond it — so a charger hovering at the
    /// aggro boundary cannot flap the state every tick.
    /// </summary>
    private const float SeparateHysteresisMeters = 1.0f;

    /// <summary>
    /// Hard cap (s) on one SEPARATE bout. The player (5.0 m/s) outruns a charger (3.5 m/s)
    /// from contact out of an 8 m aggro in well under this on open ground (net 1.5 m/s ≈ 5.4 s,
    /// less with the retreat's dodge bursts); hitting the cap means the retreat is cornered, so
    /// the pilot resumes the committed fight anyway with a loud <c>pilot: separate_timeout</c>
    /// — separation is a maneuver, never FATAL. Deliberately NOT extended by damage edges:
    /// the cap must always fire so a cornered pilot cannot be kept fleeing forever.
    /// </summary>
    private const float SeparateMaxSeconds = 6.0f;

    /// <summary>
    /// Cap on SEPARATE bouts per target commitment (Sprint 2.6K): every resume re-approaches
    /// the committed T, so the trailing charger can re-aggro inside its own range and divert
    /// again — uncapped, that resume/re-trigger flip-flop could run forever. After this many
    /// bouts against the SAME commitment the pilot fights the pincer as-is and prints a
    /// one-shot <c>pilot: separate_gaveup</c>. Re-armed only by a genuinely new commitment
    /// (<see cref="UpdateTarget"/>), never by the resume itself.
    /// </summary>
    private const int SeparateMaxAttempts = 3;

    /// <summary>
    /// Seconds a fully gate-blocked retreat (zero delta after <see cref="GateAgainstTriggers"/>)
    /// may persist inside one SEPARATE bout before the pilot counts as pinned: the one-axis
    /// slide already handles a single blocked axis, but a both-axes block — the padded entry
    /// trigger zone pinching the pilot against the south wall — would otherwise stand dead
    /// still until <see cref="SeparateMaxSeconds"/> while T keeps contact. Past this, print
    /// <c>pilot: separate_pinned</c> and take the resume path early.
    /// </summary>
    private const float SeparatePinnedSeconds = 1.0f;

    // --- EXIT / FRAGMENT geometry and timeouts ----------------------------------
    /// <summary>Half-extents of RoomTransition's trigger box (3 × 3 × 0.6 m at the arch origin).</summary>
    private const float TriggerHalfWidth = 1.5f;
    /// <summary>Body pad: the gate tests the player's CENTER path, but overlap fires when the
    /// 0.3 m capsule EDGE enters the footprint — unpadded, the gate lies by exactly that radius
    /// (the dungeon_02 phantom: center z=6.64 read 'cross=False' while the capsule at 6.94 was
    /// already inside the 6.9 footprint edge). Slabs are inflated by radius + margin.</summary>
    private const float GateBodyRadiusPad = 0.35f; // Player.tscn capsule radius 0.3 + 0.05

    private const float TriggerHalfDepth = 0.3f;
    private const float TransitionExpectSeconds = 1.0f; // the room must swap this soon after standing in the trigger
    private const float ExitWalkTimeoutSeconds = 20f;   // reaching the arch must not hang forever either
    private const float FragmentWalkTimeoutSeconds = 15f;

    // --- Trigger-footprint gate (Sprint 2.6E) -------------------------------
    /// <summary>Headroom (m/s) over the nominal move speed when previewing a step — PlayerController.MoveSpeed is authoritative and may grow; over-lookahead merely stops a tick early, under-lookahead steps into the Area and fires a swap.</summary>
    private const float GateSpeedPadding = 1.0f;

    /// <summary>Worst-case one-tick travel (m) tested against the trigger footprints before any retreat/escape/close vector is applied.</summary>
    private const float GateStepLength = (FallbackMoveSpeedMps + GateSpeedPadding) / AssumedPhysicsHz;

    /// <summary>Parametric slack (m) for the footprint segment test when one axis barely moves.</summary>
    private const float GatePathEpsilon = 0.0001f;

    // --- Threat-priority targeting (Sprint 2.6F) --------------------------------
    /// <summary>
    /// Behaviour names ranked into the charger tier for target choice and escape
    /// re-aim: chargers close distance and are the damage source, so they outrank
    /// everything else and plain distance only breaks ties within a tier. Also gates
    /// weave mode (<see cref="IsWeaveTarget"/>, Sprint 2.6G): a charger's contact box
    /// is too small to trip the reach cap, but its 0.8 s contact window still outpaces
    /// the walk band. Matched by name against the data-driven <c>EnemyBrain.Behavior</c>
    /// string (enemies.json <c>behavior</c>) — never by species or id — so a future
    /// speed-implying behaviour joins the tier by adding its name here.
    /// </summary>
    private static readonly HashSet<string> ChargerTierBehaviors = new(StringComparer.Ordinal)
    {
        "charge_player",
    };

    /// <summary>
    /// Sticky-lock re-acquire window (Sprint 2.6H): consecutive seconds a committed
    /// ENGAGE target may show no progress — NOT closing distance and NOT inside the
    /// swing gates — before ENGAGE re-picks fresh through the threat ranking above.
    /// While the window has not elapsed the committed target is never re-ranked.
    /// Deliberately longer than any self-inflicted no-progress stretch (the
    /// give-ground phase runs at most <see cref="BackTimeoutSeconds"/> before the next
    /// run-up starts closing again), so only a genuinely stuck target re-arms it.
    /// </summary>
    private const float TargetReacquireSeconds = 2.0f;

    /// <summary>
    /// Behaviour FSM states; <c>Disabled</c> is the idle/not-active state. <c>Separate</c>
    /// (Sprint 2.6J) is entered only from ENGAGE with a valid commitment and only ever
    /// returns to ENGAGE (same commitment, or the heart detour's documented fresh acquire).
    /// </summary>
    private enum PilotState { Disabled, Scan, Heart, Engage, Separate, Exit, Fragment }

    /// <summary>ENGAGE's in-and-out strike sub-cycle (shared by normal and weave mode).</summary>
    private enum EngagePhase
    {
        Close,  // run up with the facing settling on the target, swing the moment the gates open
        Strike, // one tick of standing still so this tick's press lands unambiguously
        Back,   // give ground until the next run-up distance (weave: plus dodge bursts)
    }

    private PilotState _state = PilotState.Disabled;
    private bool _debugToggleWasPressed;

    /// <summary>Actions the pilot currently holds down — the only ones it ever releases.</summary>
    private readonly HashSet<string> _heldActions = new();

    private Node3D? _player;
    private PlayerHealth? _health;
    private RoomLoader? _loader;

    // Room the pilot came from — drives forward-exit selection (exits[0] is the way back).
    private string _previousRoomId = string.Empty;

    // Room the pilot is operating in (Sprint 2.6E): re-baselined only while Disabled
    // and on EXIT-owned swaps — any other observed change is a fatal unexpected_swap.
    private string _observedRoomId = string.Empty;

    // Gate cache: Transition_* triggers of _gateRoomId. Rebuilt on room change; an empty
    // cache re-scans because triggers attach a couple of frames after a room load.
    private string _gateRoomId = string.Empty;
    private readonly List<Node3D> _gateTriggers = new();

    // --- Damage-edge escape overlay --------------------------------------------
    private int _lastHp;
    private float _escapeRemaining;

    // --- ENGAGE runtime ---------------------------------------------------------
    private EngagePhase _phase = EngagePhase.Close;
    private bool _engageWeave;          // current target is weave-mode (IsWeaveTarget)
    private ulong _targetInstanceId;    // sticky ENGAGE commitment (Sprint 2.6H); changing it re-settles facing
    private float _targetNoProgressSeconds; // committed target: consecutive no-progress time toward TargetReacquireSeconds
    private float _targetPrevTickDistance;  // committed target: last tick's distance (closing detection); MaxValue right after acquire
    private float _targetHalfExtent;    // body half-extent × scale, cached on target acquire

    // --- Stall watchdog (run #10: 18-minute silent ENGAGE stalemate) -----------
    // Any state/target/hp/movement change clears it; no change for StallFatalSeconds
    // means a pilot bug is idling the game — fail loud instead of grinding forever.
    private const float StallFatalSeconds = 30f;
    private float _stallSeconds;
    private PilotState _stallLastState = PilotState.Disabled;
    private int _stallLastHp = -1;
    private Vector3 _stallLastPos = new(9e9f, 9e9f, 9e9f);
    private float _weaveSwingReach;     // body-derived far gate for weave swings
    private float _weaveSwingNearReach; // body-derived near gate (sword's near edge)
    private int _towardTicks;           // consecutive ticks steering at the target since the last retreat/escape/target switch
    private float _backSeconds;
    private int _attackHoldTicksRemaining;
    private float _attackCooldownRemaining;
    private int _dodgeHoldTicksRemaining;
    private float _dodgePulseCooldownRemaining;

    // --- SEPARATE runtime (Sprint 2.6J, 2.6K caps) ------------------------------
    private float _separateSeconds;    // seconds in the current bout, hard-capped by SeparateMaxSeconds
    private int _separateAttempts;     // bouts run against the current commitment (cap SeparateMaxAttempts); reset on a NEW commitment, deliberately NOT on resume
    private bool _separateGaveUpLogged;// separate_gaveup already printed — once per commitment
    private float _separatePinnedSeconds; // continuous time the gated retreat delta has been fully zero this bout

    // --- HEART runtime ----------------------------------------------------------
    private Node3D? _heartTarget;
    private int _heartEntryHp;
    private float _heartWalkSeconds;
    private bool _heartFromEngage; // detour began mid-fight ⇒ resume ENGAGE (Sprint 2.6F)

    // --- EXIT runtime ------------------------------------------------------------
    private string _exitFromRoom = string.Empty;
    private string _exitExpectedRoom = string.Empty;
    private Vector3 _exitTarget; // XZ of the forward Transition_* trigger
    private float _insideTriggerSeconds;
    private float _exitWalkSeconds;

    // --- FRAGMENT runtime --------------------------------------------------------
    private float _fragmentWalkSeconds;

    public override void _Ready()
    {
        GameData.EnsureLoaded(); // forward-exit / fragment / heart discovery reads rooms.json + progression.json
        // VC_PILOT=1 enables unattended tests in the standalone device player;
        // F2 remains the manual toggle.
        if (OS.GetEnvironment("VC_PILOT") == "1")
        {
            PilotEnabled = true;
            GD.Print("GameAutopilot: enabled via VC_PILOT=1");
        }
    }

    public override void _ExitTree()
    {
        ReleaseAllActions(); // never leave an action held if the pilot is torn down mid-run
    }

    public override void _PhysicsProcess(double delta)
    {
        UpdateDebugToggleEdge();

        if (!PilotEnabled)
        {
            if (_state != PilotState.Disabled)
            {
                ReleaseAllActions(); // manual/F2 disable: no state log, nothing was printed
                _state = PilotState.Disabled;
            }

            return;
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null)
        {
            return; // autoload not up yet — retry next tick
        }

        if (!ResolveActors())
        {
            return; // loud FATAL already printed + self-disabled
        }

        // Terminal outcomes outrank the FSM: one loud line, then self-disable.
        if (gameManager.State == GameManager.GameState.GameOver)
        {
            GD.Print($"pilot: status=died room={_loader?.CurrentRoomId ?? "?"}");
            Disable();
            return;
        }

        if (gameManager.State == GameManager.GameState.Victory)
        {
            GD.Print("pilot: status=victory");
            Disable();
            return;
        }

        if (gameManager.State != GameManager.GameState.Playing)
        {
            ReleaseAllActions(); // Boot/Menu/Paused: never leave inputs held
            return;
        }

        if (!ReconcileRoomOrFatal())
        {
            return; // loud FATAL unexpected_swap already printed + self-disabled
        }

        float dt = (float)delta;

        // Stall watchdog: reset on any observable progress, FATAL on silence.
        if (_state != PilotState.Disabled)
        {
            Vector3 stallPos = _player?.GlobalPosition ?? Vector3.Zero;
            int stallHp = _health?.Hp ?? -1;
            if (_state != _stallLastState || stallHp != _stallLastHp || (stallPos - _stallLastPos).Length() > 0.05f)
            {
                _stallSeconds = 0f;
                _stallLastState = _state;
                _stallLastHp = stallHp;
                _stallLastPos = stallPos;
            }
            else
            {
                _stallSeconds += dt;
                if (_stallSeconds >= StallFatalSeconds)
                {
                    string stallRoom = _loader?.CurrentRoomId ?? string.Empty;
                    GD.Print($"pilot: FATAL engage_stalled state={_state} room={stallRoom} hp={stallHp}");
                    ReleaseAllActions();
                    Disable();
                    return;
                }
            }
        }

        TrackHpDrop();
        if (_escapeRemaining > 0f)
        {
            TickEscape(dt); // damage edge pre-empts the FSM; the FSM resumes untouched next tick
            return;
        }

        switch (_state)
        {
            case PilotState.Disabled:
                SetState(PilotState.Scan); // fresh enable: log scan on the first Playing tick
                break;
            case PilotState.Scan:
                TickScan();
                break;
            case PilotState.Heart:
                TickHeart(dt);
                break;
            case PilotState.Engage:
                TickEngage(dt);
                break;
            case PilotState.Separate:
                TickSeparate(dt);
                break;
            case PilotState.Exit:
                TickExit(dt);
                break;
            case PilotState.Fragment:
                TickFragment(dt);
                break;
        }
    }

    // ------------------------------------------------------------------
    // FSM states
    // ------------------------------------------------------------------

    /// <summary>
    /// SCAN: declared heart below the HP threshold → HEART (walked before anything
    /// else); else live enemies → ENGAGE; else win-fragment present → FRAGMENT; else → EXIT.
    /// </summary>
    private void TickScan()
    {
        RoomLoader loader = _loader!;
        if (loader.CurrentRoomId.Length == 0)
        {
            return; // first room still booting — stay in scan without log spam
        }

        if (TryPlanHeart(false))
        {
            return;
        }

        if (FindNearestEnemy() is EnemyBrain enemy)
        {
            _engageWeave = IsWeaveTarget(enemy); // status word chosen before SetState logs it
            SetState(PilotState.Engage);
            UpdateTarget(enemy); // Sprint 2.6H: SCAN's ranked pick becomes the sticky commitment on the spot (gates cached + one target= line)
            return;
        }

        if (FindWinFragment() is not null)
        {
            _fragmentWalkSeconds = 0f;
            SetState(PilotState.Fragment);
            return;
        }

        PlanExitOrFatal();
    }

    /// <summary>
    /// HEART: walk onto the room's declared live heart node. The pickup only heals
    /// while HP is below max (HeartPickup leaves full-health hearts on the floor),
    /// so the detour finishes the moment HP rises — or the node is gone — and a
    /// heart that can never be reached fails loudly. A detour planned from SCAN
    /// resumes SCAN; one planned mid-fight (Sprint 2.6F) resumes ENGAGE with a
    /// fresh target acquire.
    /// </summary>
    private void TickHeart(float dt)
    {
        if (!GodotObject.IsInstanceValid(_heartTarget))
        {
            ResumeAfterHeart(); // picked up (healed) or the room reloaded
            return;
        }

        if (_health is not null && _health.Hp > _heartEntryHp)
        {
            ResumeAfterHeart(); // heal landed
            return;
        }

        _heartWalkSeconds += dt;
        if (_heartWalkSeconds >= HeartWalkTimeoutSeconds)
        {
            FatalHeart(); // pickup never fired (sealed or unreachable) — loud instead of silent
            return;
        }

        SteerToward(_player!.GlobalPosition, _heartTarget!.GlobalPosition);
    }

    /// <summary>
    /// Leaves HEART: back to ENGAGE when the detour began mid-fight and enemies
    /// remain (Sprint 2.6F) — SetState(Engage) zeroes the target cache, then the
    /// ranked pick below commits it (Sprint 2.6H: exactly one genuine re-acquire,
    /// logged once), so the fight resumes through the same acquire/facing-settle path
    /// as any fresh engage — else back to SCAN, which re-plans heart → enemies →
    /// fragment → exit exactly as before.
    /// </summary>
    private void ResumeAfterHeart()
    {
        if (_heartFromEngage && FindNearestEnemy() is EnemyBrain enemy)
        {
            SetState(PilotState.Engage);
            UpdateTarget(enemy); // Sprint 2.6H: genuine re-acquire after the detour — ranked pick, committed (and logged) exactly once
        }
        else
        {
            SetState(PilotState.Scan);
        }
    }

    /// <summary>
    /// ENGAGE: an in-and-out strike cycle instead of standing in the band. Approach
    /// with the facing settling on the target; swing once when the gates open (reach ≤
    /// band cap, facing settled, and either outside the target's contact distance or
    /// i-framed); stand for the press tick; then give ground until the next run-up
    /// distance. Nothing is ever walked into the body: the closest the cycle gets is
    /// <c>AttackReachLimit</c> (normals) or <c>contact + <see cref="BandClearance"/></c>
    /// (weave landing), both outside contact. When the target is weave-mode — contact
    /// beyond <see cref="AttackReachLimit"/> or charger-tier behavior
    /// (<see cref="IsWeaveTarget"/>) — the mode is <c>weave</c>: dodge bursts are pulsed
    /// toward the target at each run-up (closing the 1.8 m burst, granting i-frames)
    /// and away during long retreats, and swings only fire while i-framed or already
    /// outside contact. Positions are read directly: seeing is free, acting is input-only.
    /// Target choice is sticky (Sprint 2.6H): the committed target is kept while it
    /// stays valid — see <see cref="ResolveEngageTarget"/> for the exact keep and
    /// re-acquire conditions; a fresh ranking only runs on genuine acquire/re-acquire.
    /// Pincer check (Sprint 2.6J): once this tick's commitment is locked, another live
    /// charger-tier enemy inside its own aggro range diverts the tick into SEPARATE
    /// (<see cref="TryPlanSeparate"/>) — the strike cycle pauses, the commitment rides out
    /// the detour untouched, and the weave resumes on the same body.
    /// </summary>
    private void TickEngage(float dt)
    {
        EnemyBrain? enemy = ResolveEngageTarget(dt, out float distance);
        if (enemy is null)
        {
            SetState(PilotState.Scan); // no candidate left at all — releases every held action
            return;
        }

        bool weave = IsWeaveTarget(enemy);
        bool targetChanged = UpdateTarget(enemy);
        if (weave != _engageWeave)
        {
            ResetCycle();
            _engageWeave = weave;
            GD.Print($"pilot: status={EngageWord} room={_loader?.CurrentRoomId ?? "?"} hp={HpText}");
        }
        else if (targetChanged)
        {
            ResetCycle(); // Sprint 2.6H: a genuine mid-fight re-acquire restarts the strike cycle against the new body (facing re-settles from zero)
        }

        if (TryPlanSeparate(enemy))
        {
            return; // pincer breaker (Sprint 2.6J): retreat first, strike cycle rejoins on resume
        }

        _attackCooldownRemaining = Mathf.Max(0f, _attackCooldownRemaining - dt);
        _dodgePulseCooldownRemaining = Mathf.Max(0f, _dodgePulseCooldownRemaining - dt);
        TickAttackPulse();
        TickDodgePulse();

        Vector3 playerPos = _player!.GlobalPosition; // distance came from the sticky resolution above (same tick, same positions)
        float bandFloor = enemy.ContactRange + BandClearance;

        switch (_phase)
        {
            case EngagePhase.Back:
            {
                _backSeconds += dt;
                float retreatTo = weave ? WeaveLaunchDistance(enemy) : NormalRetreatDistance(enemy);
                bool cleanLaunch = distance >= retreatTo; // timeout exits must not burst from short range: the landing would sink inside contact
                if (cleanLaunch || _backSeconds >= BackTimeoutSeconds)
                {
                    _phase = EngagePhase.Close;
                    _towardTicks = 0;
                    SteerTowardGated(playerPos, enemy.GlobalPosition);
                    if (weave && cleanLaunch)
                    {
                        StartDodgePulseIfGateClear(); // dodge-toward: re-faces, closes and i-frames the landing — skipped when the burst path would cross a gate
                    }

                    return;
                }

                SteerAwayFrom(playerPos, enemy.GlobalPosition);
                _towardTicks = 0;
                if (weave)
                {
                    StartDodgePulseIfGateClear(); // spec weave step: pulse dodge AWAY while retreating — skipped when the burst path would cross a gate (plain walking always works)
                }

                return;
            }

            case EngagePhase.Strike:
                StopSteering(); // stand so this tick's press lands exactly where the gates were measured
                _phase = EngagePhase.Back;
                _backSeconds = 0f;
                return;

            case EngagePhase.Close:
            default:
            {
                float farGate = weave ? _weaveSwingReach : AttackReachLimit;
                float nearGate = weave ? _weaveSwingNearReach : 0f;
                bool inReach = distance <= farGate && distance >= nearGate;
                bool facingSettled = _towardTicks >= FaceSettleTicks;
                bool safe = distance > enemy.ContactRange || (_health?.IsInvulnerable ?? false);
                if (facingSettled && inReach && safe
                    && _attackHoldTicksRemaining == 0 && _attackCooldownRemaining <= 0f)
                {
                    SetAction(AttackAction, true);
                    _attackHoldTicksRemaining = AttackHoldTicks;
                    StopSteering();
                    _phase = EngagePhase.Strike;
                    return;
                }

                if (distance < bandFloor)
                {
                    // Drifted inside the band without a settled facing (target switch,
                    // enemy sprinted in): give ground rather than trade inside contact.
                    _phase = EngagePhase.Back;
                    _backSeconds = 0f;
                    SteerAwayFrom(playerPos, enemy.GlobalPosition);
                    _towardTicks = 0;
                    return;
                }

                SteerTowardGated(playerPos, enemy.GlobalPosition);
                if (_heldActions.Contains(MoveUpAction) || _heldActions.Contains(MoveDownAction)
                    || _heldActions.Contains(MoveLeftAction) || _heldActions.Contains(MoveRightAction))
                {
                    _towardTicks++;
                }

                return;
            }
        }
    }

    /// <summary>
    /// SEPARATE trigger (Sprint 2.6J) — true (and transitions, logging
    /// <c>pilot: status=separate</c> once via <see cref="SetState"/>) when any OTHER live
    /// charger-tier enemy — everything but the just-locked commitment
    /// <paramref name="committed"/>, matched by the same <see cref="ChargerTierBehaviors"/>
    /// logic that ranks targets and arms the weave — stands within ITS OWN data-driven aggro
    /// range of the player: <see cref="EnemyBrain.AggroRangeMeters"/>, the public property
    /// EnemyBrain itself binds from enemies.json <c>aggro_range_m</c> in <c>BindFromData</c>
    /// (8.0 m for the skeleton charger; no record reach-through needed). Called from ENGAGE
    /// only AFTER this tick's commitment is locked, so the committed target T is always
    /// excluded by instance id — T is the fight we keep, every other charger is the pincer
    /// arm we shed. Sprint 2.6K caps this at <see cref="SeparateMaxAttempts"/> bouts per
    /// commitment: once the cap is reached a pincer arm in aggro is refused with a one-shot
    /// <c>pilot: separate_gaveup</c> and the pilot fights the pincer as-is. The companion
    /// hold/exit ladder lives in <see cref="TickSeparate"/>; the full-swarm retreat VECTOR
    /// is built there, not here (the trigger stays others-only).
    /// </summary>
    private bool TryPlanSeparate(EnemyBrain committed)
    {
        Node3D? container = FindCurrentRoomNode()?.GetNodeOrNull<Node3D>(EnemiesNodeName);
        if (container is null)
        {
            return false; // mid-load / room gone — no pincer to break (SCAN would treat it as cleared)
        }

        Vector3 playerPos = _player!.GlobalPosition;
        ulong committedId = committed.GetInstanceId();
        foreach (Node child in container.GetChildren())
        {
            if (child is not EnemyBrain enemy
                || !GodotObject.IsInstanceValid(enemy)
                || !enemy.IsInsideTree()
                || enemy.Hp <= 0
                || enemy.GetInstanceId() == committedId
                || !IsChargerTier(enemy))
            {
                continue; // dead / the committed target T (the one we keep) / not charger-tier
            }

            if (PlanarDistance(playerPos, enemy.GlobalPosition) > enemy.AggroRangeMeters)
            {
                continue; // outside its own aggro — not part of the pincer (yet)
            }

            if (_separateAttempts >= SeparateMaxAttempts)
            {
                if (!_separateGaveUpLogged)
                {
                    _separateGaveUpLogged = true; // loud, once per commitment: fight the pincer as-is — the resume/re-aggro flip-flop is capped (Sprint 2.6K)
                    GD.Print("pilot: separate_gaveup");
                }

                return false;
            }

            _separateAttempts++;
            _separateSeconds = 0f;
            _separatePinnedSeconds = 0f;
            SetState(PilotState.Separate); // releases inputs + logs status=separate once; _targetInstanceId untouched (sticky T survives the detour)
            return true;
        }

        return false;
    }

    /// <summary>
    /// SEPARATE (Sprint 2.6J, vector corrected in 2.6K) — the pincer breaker. Retreats along
    /// the gated steer away from the XZ CENTROID of the WHOLE live charger swarm — the
    /// committed target T INCLUDED (2.6K: T chases at 3.5 m/s like any other arm, so the
    /// original others-only centroid excluded T and aimed the pilot straight into T's
    /// contact box), pulsing burst-gated dodge to cover ground. The HOLD/EXIT ladder still
    /// judges only the OTHER chargers — breaking the second arm's aggro is the point of the
    /// maneuver: the retreat ends once every one of THEM is beyond its OWN aggro +
    /// <see cref="SeparateHysteresisMeters"/> of the player — the human outrun maneuver:
    /// player 5.0 m/s vs charger 3.5 m/s, so the trailing charger falls out of its
    /// data-driven aggro and walks home, leaving the committed target to be finished 1v1
    /// (T may legitimately still be inside its aggro at resume; the weave's Close/walk-in
    /// path approaches it from the grown range and <see cref="ResetCycle"/> re-settles
    /// facing on the way back).
    /// Composition: the retreat gate-checks the swarm-centroid vector through
    /// <see cref="GateAgainstTriggers"/> directly (SEPARATE needs the POST-gate delta to
    /// detect a pin, so it inlines the two lines <see cref="SteerAwayFrom"/> wraps), and
    /// the dodge rides <see cref="StartDodgePulseIfGateClear"/> so the burst gate applies
    /// too. The damage-edge escape overlay is SUBSUMED while this state owns the steering
    /// (see <see cref="TrackHpDrop"/> — its away-aim would swap this swarm centroid for a
    /// nearest-enemy aim that may be T itself), while the hp≤2 heart detour still opens.
    /// Exits: no other charger within the hold range ⇒ resume on the SAME commitment; a
    /// cornered retreat hits <see cref="SeparateMaxSeconds"/> ⇒ loud
    /// <c>pilot: separate_timeout</c>; a retreat whose gated delta stays fully zero past
    /// <see cref="SeparatePinnedSeconds"/> ⇒ loud <c>pilot: separate_pinned</c> and an early
    /// resume through the same path (2.6K) — never fatal, no action held.
    /// </summary>
    private void TickSeparate(float dt)
    {
        _separateSeconds += dt;
        TickDodgePulse(); // finish any pulse still in its hold window

        Node3D? container = FindCurrentRoomNode()?.GetNodeOrNull<Node3D>(EnemiesNodeName);
        Vector3 playerPos = _player!.GlobalPosition;
        Vector3 swarmCentroid = Vector3.Zero;
        int swarm = 0;
        bool anyWithinHoldRange = false;
        if (container is not null)
        {
            foreach (Node child in container.GetChildren())
            {
                if (child is not EnemyBrain enemy
                    || !GodotObject.IsInstanceValid(enemy)
                    || !enemy.IsInsideTree()
                    || enemy.Hp <= 0
                    || !IsChargerTier(enemy))
                {
                    continue; // dead / not charger-tier
                }

                swarm++;
                swarmCentroid += enemy.GlobalPosition; // steering sees the WHOLE swarm, committed T included (Sprint 2.6K)

                if (enemy.GetInstanceId() == _targetInstanceId)
                {
                    continue; // T feeds the retreat vector but never the hold/exit ladder — shaking off the OTHER arm is the point
                }

                float distance = PlanarDistance(playerPos, enemy.GlobalPosition);
                if (distance <= enemy.AggroRangeMeters + SeparateHysteresisMeters)
                {
                    anyWithinHoldRange = true; // still on our tail — keep the retreat
                }
            }
        }

        if (!anyWithinHoldRange)
        {
            ResumeAfterSeparate(); // every other charger shaken off (or gone) — back to the committed weave
            return;
        }

        if (_separateSeconds >= SeparateMaxSeconds)
        {
            GD.Print("pilot: separate_timeout"); // cornered: loud, then resume anyway — separation never fatals
            ResumeAfterSeparate();
            return;
        }

        // Retreat away from the centroid of the WHOLE swarm (T included — Sprint 2.6K: an
        // others-only aim walked the pilot INTO the committed T). swarm >= 1 is guaranteed
        // here: anyWithinHoldRange above requires a live OTHER charger counted in this same
        // loop. The gate runs before the steer so the post-gate delta stays visible: a
        // single blocked axis keeps its slide (GateAgainstTriggers' one-axis fallback), a
        // fully zeroed delta is the entry-corner pin counted against
        // SeparatePinnedSeconds. The steer must precede the dodge pulse: PlayerController
        // reads the held move vector at the dodge's rising edge, while
        // StartDodgePulseIfGateClear refuses a burst whose whole 1.8 m path crosses a gate
        // (and a zero hold — pinned — skips it for the same unverifiable-direction reason).
        Vector3 delta = playerPos - swarmCentroid / swarm;
        delta.Y = 0f;
        GateAgainstTriggers(playerPos, ref delta);
        ApplySteer(delta);

        if (delta == Vector3.Zero)
        {
            _separatePinnedSeconds += dt;
            if (_separatePinnedSeconds > SeparatePinnedSeconds)
            {
                GD.Print("pilot: separate_pinned"); // both axes gate-blocked — force the timeout path early instead of standing dead still while T keeps contact
                ResumeAfterSeparate();
            }

            return; // pinned this tick: nothing verifiable to burst along — hold and count
        }

        _separatePinnedSeconds = 0f;
        StartDodgePulseIfGateClear();
    }

    /// <summary>
    /// Leaves SEPARATE back onto the SAME commitment: the clean input slate and cycle reset
    /// <c>SetState(Engage)</c> would take, but WITHOUT its fresh-acquire bookkeeping — the
    /// sticky lock (<see cref="_targetInstanceId"/>), the cached weave gates and
    /// <see cref="_engageWeave"/> all survive the detour, so no second <c>target=</c> line
    /// logs and the strike cycle resumes on T itself (ResetCycle re-settles facing after the
    /// run; the no-progress window is re-seeded so the first return tick counts as closing —
    /// the detour necessarily grew the distance). The state log IS the engage resume line.
    /// Sprint 2.6K: <see cref="_separateAttempts"/> deliberately does NOT reset here — the
    /// attempt cap is per COMMITMENT (re-armed only by <see cref="UpdateTarget"/>), so a
    /// resume whose trailing charger re-aggros and re-diverts is bounded at
    /// <see cref="SeparateMaxAttempts"/> bouts before the pilot fights the pincer as-is.
    /// Should the commitment have vanished while we ran (not reachable today — nothing else
    /// damages enemies — but honored anyway), the next <see cref="TickEngage"/> resolves a
    /// genuine fresh acquire or SCAN exactly as any other loss of commitment would.
    /// </summary>
    private void ResumeAfterSeparate()
    {
        ReleaseAllActions();
        _state = PilotState.Engage;
        ResetCycle();
        _attackHoldTicksRemaining = 0;
        _attackCooldownRemaining = 0f;
        _dodgeHoldTicksRemaining = 0;
        _dodgePulseCooldownRemaining = 0f;
        _targetNoProgressSeconds = 0f;
        _targetPrevTickDistance = float.MaxValue;
        GD.Print($"pilot: status=engage room={_loader?.CurrentRoomId ?? "?"} hp={HpText}"); // resume line — commitment untouched, no target= follows
    }

    /// <summary>
    /// EXIT: walk into the forward arch trigger and *expect* the room to swap. Standing
    /// inside the trigger footprint for <see cref="TransitionExpectSeconds"/> without a
    /// swap — or never reaching the arch at all — is a FATAL advance_missed; arriving in
    /// a room other than the expected one is too.
    /// </summary>
    private void TickExit(float dt)
    {
        RoomLoader loader = _loader!;

        // Did the room actually change? (The Area fires slightly before the strict
        // footprint test below, so this check comes first.)
        if (loader.CurrentRoomId != _exitFromRoom)
        {
            if (loader.CurrentRoomId == _exitExpectedRoom)
            {
                GD.Print($"pilot: status=transition from={_exitFromRoom} to={_exitExpectedRoom}");
                _previousRoomId = _exitFromRoom; // next room's forward exit = "not where I came from"
                SetState(PilotState.Scan);
            }
            else
            {
                FatalAdvanceMissed();
            }

            return;
        }

        Vector3 playerPos = _player!.GlobalPosition;
        if (InsideTriggerFootprint(playerPos, _exitTarget))
        {
            StopSteering();
            _insideTriggerSeconds += dt;
            if (_insideTriggerSeconds >= TransitionExpectSeconds)
            {
                FatalAdvanceMissed(); // walked through the trigger; RoomLoader never swapped
            }
        }
        else
        {
            _insideTriggerSeconds = 0f;
            _exitWalkSeconds += dt;
            if (_exitWalkSeconds >= ExitWalkTimeoutSeconds)
            {
                FatalAdvanceMissed(); // never reached the arch (blocked, or the path is sealed)
            }

            SteerToward(playerPos, _exitTarget);
        }
    }

    /// <summary>FRAGMENT: with the room cleared, walk onto the win item — CrownFragmentPickup raises Victory itself.</summary>
    private void TickFragment(float dt)
    {
        Node3D? fragment = FindWinFragment();
        if (fragment is null)
        {
            FatalFragment(); // win node vanished mid-walk — impossible state, never idle in limbo
            return;
        }

        _fragmentWalkSeconds += dt;
        if (_fragmentWalkSeconds >= FragmentWalkTimeoutSeconds)
        {
            FatalFragment(); // pickup never fired (sealed or unreachable) — loud instead of silent
            return;
        }

        SteerToward(_player!.GlobalPosition, fragment.GlobalPosition);
    }

    // ------------------------------------------------------------------
    // Damage-edge escape overlay (Sprint 2.6D)
    // ------------------------------------------------------------------

    /// <summary>
    /// Compares HP against the previous tick; on a drop, releases the move actions
    /// and opens the escape window (death itself is handled by the GameOver terminal
    /// line). Sprint 2.6F: the same edge, taken by ENGAGE at or below
    /// <see cref="MidFightHeartHpThreshold"/>, also plans the room's declared heart
    /// detour — a false return (no heart declared, or its node already gone) simply
    /// keeps the fight going, never fatal. Sprint 2.6J widens that heart edge to SEPARATE,
    /// and carves SEPARATE out of the escape window: while the pincer retreat owns the
    /// steering, opening escape would swap the swarm-centroid retreat aim for a
    /// nearest-enemy aim that may be the committed target T itself — so the edge only
    /// drops the swing cadence and re-settles facing here, while TickSeparate's own
    /// gated steer + gated dodge pulses carry the retreat (the escape overlay is thus
    /// SUBSUMED, not stacked). The SeparateMaxSeconds cap is never extended by an edge:
    /// cornered must still time out into a resume, and nothing here is ever fatal.
    /// </summary>
    private void TrackHpDrop()
    {
        if (_health is null)
        {
            return;
        }

        int hp = _health.Hp;
        bool damaged = hp < _lastHp && hp > 0;
        if (damaged && _escapeRemaining <= 0f && _state != PilotState.Separate)
        {
            _escapeRemaining = EscapeSeconds;
            StopSteering(); // spec: on the damage edge, release all move actions ...
            SetAction(AttackAction, false);
            _towardTicks = 0; // escape turns the body away — the strike cycle must re-settle its facing
        }
        else if (damaged && _state == PilotState.Separate)
        {
            // Sprint 2.6J: SEPARATE subsumes the escape overlay — TickSeparate re-steers
            // away-from-the-swarm (and fires the gated dodge) THIS very tick, so the edge only
            // drops the swing cadence and re-settles facing; the retreat aim is never
            // released and the SeparateMaxSeconds clock is untouched (cap stays hard).
            SetAction(AttackAction, false);
            _towardTicks = 0;
        }

        if (damaged && (_state == PilotState.Engage || _state == PilotState.Separate) && hp <= MidFightHeartHpThreshold)
        {
            TryPlanHeart(true); // false ⇒ nothing to detour through — keep fighting (heart detour reachable from SEPARATE too, Sprint 2.6J)
        }

        _lastHp = hp;
    }

    /// <summary>
    /// One escape tick: re-aim directly away from the threat-ranked nearest enemy
    /// every tick (Sprint 2.6F — a charger outranks a closer wanderer, so a stray
    /// fodder enemy can no longer flip the escape into the swarm) and pulse
    /// <c>dodge</c> (direction read by PlayerController at the rising edge, so the
    /// away-hold always precedes the press). The pulse retries each tick so a
    /// still-cooling dodge fires the moment it becomes ready inside the window; a
    /// refused pulse degrades to plain walking, never to standing still — the
    /// steering below is re-applied unconditionally every tick of the window, so the
    /// walk lasts the full <see cref="EscapeSeconds"/> unless a trigger footprint
    /// blocks that direction (the gate's slide/stop, not an early give-up). When the
    /// window closes, everything is released and the FSM resumes untouched.
    /// </summary>
    private void TickEscape(float dt)
    {
        TickDodgePulse(); // finish any pulse still in its hold window

        // Re-steer every tick of the window: even when StartDodgePulseIfGateClear
        // below refuses the burst (still cooling, or its 1.8 m path crosses an
        // arch), the away-hold itself keeps walking for the rest of the escape.
        if (FindNearestEnemy() is EnemyBrain enemy)
        {
            SteerAwayFrom(_player!.GlobalPosition, enemy.GlobalPosition);
        }
        else
        {
            StopSteering(); // no threat in the room — direction is meaningless, keep releasing
        }

        StartDodgePulseIfGateClear(); // gate- and refractory-cleared: at most one burst-safe pulse per DodgeCooldownSeconds

        _escapeRemaining -= dt;
        if (_escapeRemaining <= 0f)
        {
            StopSteering();
            SetAction(AttackAction, false);
            ReleaseDodgeIfHeld();
        }
    }

    // ------------------------------------------------------------------
    // Planning / discovery (declaration-driven — data and scene names only)
    // ------------------------------------------------------------------

    /// <summary>
    /// Heart detour gate (all three conditions required, else silently false): HP at or
    /// below <see cref="HeartHpThreshold"/> and under max, the current room declares
    /// the <c>heart</c> item in rooms.json, and a live node carrying that literal id
    /// (with a HeartPickup somewhere beneath it) exists in the room. Plans HEART and
    /// logs <c>status=heart</c>; a full heart is skipped without a word.
    /// <paramref name="fromEngage"/> records where the detour was planned from
    /// (Sprint 2.6F): mid-fight edges resume ENGAGE on completion, scan-time plans
    /// resume SCAN.
    /// </summary>
    private bool TryPlanHeart(bool fromEngage)
    {
        if (_health is null || _health.Hp > HeartHpThreshold || _health.Hp >= _health.HpMax)
        {
            return false;
        }

        if (!RoomDeclaresHeart())
        {
            return false;
        }

        Node3D? heart = FindHeartNode();
        if (heart is null)
        {
            return false; // already collected (or never placed) — proceed normally
        }

        _heartTarget = heart;
        _heartEntryHp = _health.Hp;
        _heartWalkSeconds = 0f;
        _heartFromEngage = fromEngage;
        SetState(PilotState.Heart);
        return true;
    }

    /// <summary>True when the current room's rooms.json record declares the heart entry (id taken literally, never PascalCased).</summary>
    private bool RoomDeclaresHeart()
    {
        if (_loader is null)
        {
            return false;
        }

        foreach (RoomRecord room in GameData.Rooms)
        {
            if (room.Id != _loader.CurrentRoomId)
            {
                continue;
            }

            foreach (RoomItemRecord item in room.Items)
            {
                if (string.Equals(item.Id, HeartItemId, StringComparison.Ordinal)
                    || string.Equals(item.EffectiveItemId, HeartItemId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>The room's live heart body: a node named with the literal item id that actually hosts a HeartPickup (null when absent or already freed).</summary>
    private Node3D? FindHeartNode()
    {
        Node3D? room = FindCurrentRoomNode();
        if (room is null)
        {
            return null;
        }

        Node? found = FindDescendant(
            room,
            child => string.Equals(child.Name.ToString(), HeartItemId, StringComparison.Ordinal)
                && FindDescendant(child, nested => nested is HeartPickup) is not null);
        return found as Node3D;
    }

    /// <summary>Resolves the forward exit + its runtime-attached trigger, or fails with FATAL advance_missed.</summary>
    private void PlanExitOrFatal()
    {
        RoomLoader loader = _loader!;
        _exitFromRoom = loader.CurrentRoomId;
        _exitExpectedRoom = ResolveForwardTargetId(_exitFromRoom);

        Node3D? trigger = _exitExpectedRoom.Length > 0
            ? FindTransitionTrigger(_exitExpectedRoom)
            : null;
        if (trigger is null)
        {
            FatalAdvanceMissed(); // no declared forward exit, or no arch for it — impossible state
            return;
        }

        _exitTarget = trigger.GlobalPosition;
        _insideTriggerSeconds = 0f;
        _exitWalkSeconds = 0f;
        SetState(PilotState.Exit);
    }

    /// <summary>
    /// Forward exit of <paramref name="fromRoom"/> per rooms.json: the first exit whose
    /// target is NOT the room the pilot came from (exits[0] is the way back — the pilot
    /// tracks <c>_previousRoomId</c> itself). With no tracked origin (fresh enable in
    /// mid-run) prefer exits[1], the documented "way on", else the only exit. If every
    /// declared exit leads back there is no forward path — empty, which fails loudly
    /// rather than backtracking. No room ids are hardcoded anywhere.
    /// </summary>
    private string ResolveForwardTargetId(string fromRoom)
    {
        foreach (RoomRecord room in GameData.Rooms)
        {
            if (room.Id != fromRoom)
            {
                continue;
            }

            if (_previousRoomId.Length > 0)
            {
                foreach (ExitRecord exit in room.Exits)
                {
                    if (exit.RoomId != _previousRoomId)
                    {
                        return exit.RoomId;
                    }
                }

                return string.Empty; // every exit leads back — no forward path
            }

            if (room.Exits.Count > 1)
            {
                return room.Exits[1].RoomId;
            }

            return room.Exits.Count > 0 ? room.Exits[0].RoomId : string.Empty;
        }

        return string.Empty;
    }

    /// <summary>Finds the runtime-attached exit trigger RoomTransition named <c>Transition_&lt;roomId&gt;</c> (RoomTransition.AttachAll naming) anywhere under Main.</summary>
    private Node3D? FindTransitionTrigger(string targetRoomId)
    {
        string wanted = TransitionNodePrefix + targetRoomId;
        Node? found = FindDescendant(
            GetParent(),
            child => child is RoomTransition
                && string.Equals(child.Name.ToString(), wanted, StringComparison.Ordinal));
        return found as Node3D;
    }

    /// <summary>The win item (progression.json win_condition → node name via PascalCase, as CrownFragmentPickup resolves it) — null in rooms without one.</summary>
    private Node3D? FindWinFragment()
    {
        WinConditionRecord? win = GameData.Progression?.WinCondition;
        if (win is null || _loader is null || win.RoomId != _loader.CurrentRoomId)
        {
            return null;
        }

        string nodeName = RoomLoader.ToPascalCase(win.ItemId);
        Node? found = FindDescendant(GetParent(), child => string.Equals(child.Name.ToString(), nodeName, StringComparison.Ordinal));
        return found as Node3D;
    }

    /// <summary>
    /// Weave gate (Sprint 2.6G): true for a target whose contact box beyond the reach
    /// cap forces the dodge weave (the boss) OR whose behavior is charger-tier
    /// (<see cref="ChargerTierBehaviors"/>) — a charger's 0.9 m contact sits below
    /// <see cref="AttackReachLimit"/>, but contact damage accrues every 0.8 s while the
    /// walk band only breaks contact once per cycle, so the trade math that loses rooms
    /// of HP against chargers is exactly what the weave exists to prevent. Data-driven
    /// (contact geometry + behavior name), no ids or species.
    /// </summary>
    private static bool IsWeaveTarget(EnemyBrain enemy) =>
        enemy.ContactRange > AttackReachLimit || IsChargerTier(enemy);

    /// <summary>True when the enemy fights from the charger tier (data-driven behavior name; empty never matches).</summary>
    private static bool IsChargerTier(EnemyBrain enemy) =>
        !string.IsNullOrEmpty(enemy.Behavior) && ChargerTierBehaviors.Contains(enemy.Behavior);

    /// <summary>
    /// Threat-ranked live EnemyBrain under the current room's <c>Enemies</c>
    /// container (Sprint 2.6F): charger-tier behaviours first
    /// (<see cref="ChargerTierBehaviors"/>, matched by name on the data-driven
    /// <c>Behavior</c> string), then nearest XZ distance within the tier. Drives
    /// SCAN's initial pick, ENGAGE's acquire/re-acquire, and the escape overlay's
    /// per-tick re-aim — never the sticky ENGAGE keep-check (Sprint 2.6H), which
    /// validates the committed target without re-ranking the alternatives.
    /// </summary>
    private EnemyBrain? FindNearestEnemy()
    {
        Node3D? room = FindCurrentRoomNode();
        Node3D? container = room?.GetNodeOrNull<Node3D>(EnemiesNodeName);
        if (container is null)
        {
            return null; // no container ⇒ room treated as cleared (RoomLoader convention)
        }

        Vector3 playerPos = _player!.GlobalPosition;
        EnemyBrain? nearest = null;
        bool nearestIsCharger = false;
        float nearestDistance = float.MaxValue;
        foreach (Node child in container.GetChildren())
        {
            if (child is not EnemyBrain enemy
                || !GodotObject.IsInstanceValid(enemy)
                || !enemy.IsInsideTree()
                || enemy.Hp <= 0)
            {
                continue; // not an enemy, or already dead and about to leave the tree
            }

            float distance = PlanarDistance(playerPos, enemy.GlobalPosition);
            bool charger = IsChargerTier(enemy);
            bool better = nearest is null
                || (charger && !nearestIsCharger) // tier first: chargers close distance and deal the damage
                || (charger == nearestIsCharger && distance < nearestDistance); // then distance within tier
            if (better)
            {
                nearestDistance = distance;
                nearestIsCharger = charger;
                nearest = enemy;
            }
        }

        return nearest;
    }

    /// <summary>The current room: the Main child that owns an <c>Enemies</c> container (rooms are the only such siblings).</summary>
    private Node3D? FindCurrentRoomNode()
    {
        Node? parent = GetParent();
        if (parent is null)
        {
            return null;
        }

        foreach (Node child in parent.GetChildren())
        {
            if (child != this && child is Node3D room && room.HasNode(EnemiesNodeName))
            {
                return room;
            }
        }

        return null;
    }

    /// <summary>Depth-first search through <paramref name="root"/>'s subtree for the first node matching <paramref name="match"/>.</summary>
    private static Node? FindDescendant(Node? root, Func<Node, bool> match)
    {
        if (root is null)
        {
            return null;
        }

        foreach (Node child in root.GetChildren())
        {
            if (match(child))
            {
                return child;
            }

            Node? nested = FindDescendant(child, match);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Strike-cycle geometry / target bookkeeping
    // ------------------------------------------------------------------

    /// <summary>
    /// Sticky-lock resolution (Sprint 2.6H) — returns the target ENGAGE fights this
    /// tick. The committed target (<see cref="_targetInstanceId"/>) is KEPT while it
    /// still satisfies every keep-condition: a live, in-tree EnemyBrain that is still
    /// a child of the current room's <c>Enemies</c> container. A fresh threat-ranked
    /// pick (<see cref="FindNearestEnemy"/>: charger tier, then distance) happens only
    /// when there is no valid commitment (fresh engage, or the target died, was freed,
    /// or left the room) or when the commitment sustains
    /// <see cref="TargetReacquireSeconds"/> of no progress — not closing distance and
    /// not inside the swing gates (walled off, boxed in, unreachable). Returns null
    /// only when no candidate exists at all (⇒ SCAN). The out distance is the planar
    /// player→target distance for the returned target, measured this tick.
    /// </summary>
    private EnemyBrain? ResolveEngageTarget(float dt, out float distance)
    {
        Vector3 playerPos = _player!.GlobalPosition;
        EnemyBrain? committed = FindCommittedTarget();
        if (committed is null)
        {
            _targetNoProgressSeconds = 0f;
            EnemyBrain? acquired = FindNearestEnemy(); // genuine acquire: rank fresh
            distance = acquired is null ? 0f : PlanarDistance(playerPos, acquired.GlobalPosition);
            return acquired;
        }

        distance = PlanarDistance(playerPos, committed.GlobalPosition);

        // In-reach against the commitment's own gates: the weave gates are cached per
        // commitment in UpdateTarget, the normal gate is the reach cap constant.
        bool weaveGates = IsWeaveTarget(committed);
        float farGate = weaveGates ? _weaveSwingReach : AttackReachLimit;
        float nearGate = weaveGates ? _weaveSwingNearReach : 0f;
        bool inReach = distance <= farGate && distance >= nearGate;
        bool closing = distance < _targetPrevTickDistance;
        _targetPrevTickDistance = distance;

        if (inReach || closing)
        {
            _targetNoProgressSeconds = 0f; // progress — the sticky lock holds unconditionally
            return committed;
        }

        _targetNoProgressSeconds += dt;
        if (_targetNoProgressSeconds < TargetReacquireSeconds)
        {
            return committed; // sticky: no re-ranking inside the window
        }

        // Sustained no progress: re-pick fresh through the ranking. If the ranking
        // still selects this same instance, the commitment (and its single target=
        // line) simply continues — no flip, no cycle restart, window rearmed.
        _targetNoProgressSeconds = 0f;
        _targetPrevTickDistance = float.MaxValue;
        EnemyBrain? reacquired = FindNearestEnemy() ?? committed;
        distance = PlanarDistance(playerPos, reacquired.GlobalPosition);
        return reacquired;
    }

    /// <summary>
    /// The committed ENGAGE target when it still satisfies every sticky keep-condition
    /// (Sprint 2.6H): a live (Hp &gt; 0), in-tree EnemyBrain that is STILL a child of the
    /// current room's <c>Enemies</c> container. Null when there is no commitment (id 0
    /// after SetState) or the target died, was freed, or left the room — the only
    /// validity-based reasons to rank a fresh pick.
    /// </summary>
    private EnemyBrain? FindCommittedTarget()
    {
        if (_targetInstanceId == 0)
        {
            return null;
        }

        Node3D? container = FindCurrentRoomNode()?.GetNodeOrNull<Node3D>(EnemiesNodeName);
        if (container is null)
        {
            return null; // no container ⇒ nothing can be committed to (room gone / mid-load)
        }

        foreach (Node child in container.GetChildren())
        {
            if (child is EnemyBrain enemy
                && enemy.GetInstanceId() == _targetInstanceId
                && GodotObject.IsInstanceValid(enemy)
                && enemy.IsInsideTree()
                && enemy.Hp > 0)
            {
                return enemy;
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the weave swing gates and the facing-settle bookkeeping in step with the
    /// current target: caches the body's widest horizontal half-extent × scale (the
    /// same geometry EnemyBrain.ContactRange derives from) on target acquire, restarts
    /// the sticky no-progress window (Sprint 2.6H — this is the single point where a
    /// new commitment is made, so the <c>target=</c> line logs once per commitment),
    /// and reports whether the target actually changed so the caller can re-settle
    /// facing.
    /// </summary>
    private bool UpdateTarget(EnemyBrain enemy)
    {
        ulong id = enemy.GetInstanceId();
        if (id == _targetInstanceId)
        {
            return false;
        }

        _targetInstanceId = id;
        _targetHalfExtent = ReadHalfExtent(enemy);
        _targetNoProgressSeconds = 0f;            // Sprint 2.6H: a fresh commitment starts a fresh re-acquire window
        _targetPrevTickDistance = float.MaxValue; // first tick after acquire counts as closing (distance can only shrink)
        _separateAttempts = 0;                    // Sprint 2.6K: the SEP attempt cap is per COMMITMENT — re-armed only here, never by the resume itself
        _separateGaveUpLogged = false;
        _separatePinnedSeconds = 0f;
        GD.Print($"pilot: target={enemy.Name} behavior={(string.IsNullOrEmpty(enemy.Behavior) ? "?" : enemy.Behavior)}"); // logged once per commitment — genuine acquire or re-acquire only

        // Weave far gate: the body's surface must sit inside the sword box (half +
        // tip) with margin; fall back to the band itself if the shape is unreadable,
        // which keeps every weave swing outside contact either way.
        _weaveSwingReach = _targetHalfExtent > 0f
            ? _targetHalfExtent + SwordTipReach - SwingSafetyMargin
            : enemy.ContactRange + BandClearance;
        _weaveSwingNearReach = _targetHalfExtent > 0f
            ? _targetHalfExtent + SwordNearReach + SwingSafetyMargin
            : 0f;
        return true;
    }

    /// <summary>Widest horizontal box half-edge scaled with the instance — 0 when the collision shape is missing (falls back to band-relative gates).</summary>
    private static float ReadHalfExtent(EnemyBrain enemy)
    {
        if (enemy.GetNodeOrNull<CollisionShape3D>("CollisionShape3D")?.Shape is not BoxShape3D box)
        {
            return 0f;
        }

        float scaleMax = Mathf.Max(Mathf.Abs(enemy.Scale.X), Mathf.Abs(enemy.Scale.Z));
        return Mathf.Max(box.Size.X, box.Size.Z) * 0.5f * scaleMax;
    }

    /// <summary>
    /// Normal-mode give-ground distance: past the swing cap by exactly enough full
    /// retreat-to-re-face run-up that the next approach crosses the cap with the
    /// facing already settled (FaceSettleTicks × closing step + padding), against this
    /// enemy's live speed — data-driven, no per-enemy constants.
    /// </summary>
    private static float NormalRetreatDistance(EnemyBrain enemy) =>
        AttackReachLimit
        + FaceSettleTicks * (FallbackMoveSpeedMps + enemy.CurrentSpeedMps) / AssumedPhysicsHz
        + RetreatPadding;

    /// <summary>
    /// Weave dodge-toward launch distance: far enough back that the fixed 1.8 m burst
    /// (plus the enemy's own closing during it) lands the player at
    /// <c>contact + <see cref="BandClearance"/></c> — the band — instead of against the
    /// body, so the i-framed strike and every step of the exit happen outside contact.
    /// Every term is contact-relative, so the launch degrades with the target's actual
    /// geometry: the boss (contact 1.8, closing 1.8 × 0.27) launches at ≈4.02 m, a
    /// charger (contact 0.9, closing 1.8 × 0.525) at ≈3.375 m — both land at
    /// <c>contact + BandClearance</c>, never at the collision-stop where i-frames
    /// would expire before contact can be re-crossed (2 damage per cycle at phase-2
    /// speed — which cannot outlast 15 hits).
    /// </summary>
    private static float WeaveLaunchDistance(EnemyBrain enemy) =>
        enemy.ContactRange
        + DodgeBurstSpeedMps * DodgeBurstSeconds
        + enemy.CurrentSpeedMps * DodgeBurstSeconds
        + BandClearance;

    private void ResetCycle()
    {
        _phase = EngagePhase.Close;
        _towardTicks = 0;
        _backSeconds = 0f;
    }

    // ------------------------------------------------------------------
    // Trigger-footprint gate + swap guard (Sprint 2.6E)
    // ------------------------------------------------------------------

    /// <summary>
    /// Swap guard: outside the EXIT state the pilot owns no transition, so an observed
    /// <see cref="RoomLoader.CurrentRoomId"/> change means something walked through an
    /// arch behind its back (retreat through an always-open entry arch, or a
    /// stale-overlap phantom). Fail loudly with both room ids and disable instead of
    /// continuing to plan exits against a room that is gone. While Disabled the
    /// baseline re-forms (a human playing with the pilot off must not trip fatals), and
    /// the EXIT state re-baselines because TickExit validates that swap itself.
    /// </summary>
    private bool ReconcileRoomOrFatal()
    {
        string current = _loader!.CurrentRoomId;
        if (current.Length == 0 || _state == PilotState.Disabled)
        {
            _observedRoomId = current;
            return true;
        }

        if (_observedRoomId.Length == 0 || current == _observedRoomId)
        {
            return true;
        }

        if (_state == PilotState.Exit)
        {
            _observedRoomId = current; // EXIT-owned swap; TickExit reports or fatals it
            return true;
        }

        GD.Print($"pilot: FATAL unexpected_swap from={_observedRoomId} to={current}");
        ReleaseAllActions();
        Disable();
        return false;
    }

    /// <summary>
    /// Zeroes any component of a planar move vector that would carry the player into a
    /// Transition trigger footprint of the current room (the Area3D's actual 3 × 0.6 m
    /// XZ box at each trigger's global position). On a crossing, whichever single-axis
    /// move misses the footprint is kept — the retreat slides along the gate's edge —
    /// and if neither axis is clear the vector stops dead rather than clipping a corner.
    /// The EXIT state passes vectors through untouched: crossing an arch is its job.
    /// </summary>
    private void GateAgainstTriggers(Vector3 from, ref Vector3 delta)
    {
        if (_state == PilotState.Exit || delta == Vector3.Zero)
        {
            return;
        }

        Vector3 step = delta.Normalized() * GateStepLength;
        bool cross = PathCrossesTrigger(from, from + step);
        if (!cross)
        {
            return; // one tick's worst-case travel stays clear — full vector
        }

        bool crossX = PathCrossesTrigger(from, from + new Vector3(step.X, 0f, 0f));
        bool crossZ = PathCrossesTrigger(from, from + new Vector3(0f, 0f, step.Z));
        if (!crossX && (crossZ || Mathf.Abs(delta.X) >= Mathf.Abs(delta.Z)))
        {
            delta.Z = 0f; // keep the X slide (the larger component when both are clear)
        }
        else if (!crossZ)
        {
            delta.X = 0f; // keep the Z slide
        }
        else
        {
            delta = Vector3.Zero; // both axes blocked — stop at the gate
        }
    }

    /// <summary>
    /// True when the straight XZ path between the points enters any Transition trigger
    /// footprint of the current room. Segment-tested rather than endpoint-tested so the
    /// 1.8 m dodge burst cannot clip a footprint its landing point sails past (a
    /// one-tick walk step is far shorter than the box, so its landing test is exact).
    /// </summary>
    private bool PathCrossesTrigger(Vector3 from, Vector3 to)
    {
        foreach (Node3D trigger in GateTriggers())
        {
            Vector3 origin = trigger.GlobalPosition;
            // Inside the safety envelope (raw box + body-radius pad): the center is
            // already past the approach barrier, and the firing line (center = raw −
            // radius) is close behind. Forbid ONLY movement deeper toward the arch
            // plane — retreat and parallel slides stay free. Two prior bugs bracket
            // this rule: a padded test from an inside start reads cross=True for every
            // vector (run #10: 18-minute deadlock parked at z=6.59), while a raw
            // inside-barrier let the center creep to the firing line (run #11: swap).
            // Arches all face ±Z (RoomTransition's documented geometry).
            if (InsideBox(from, origin, TriggerHalfWidth + GateBodyRadiusPad, TriggerHalfDepth + GateBodyRadiusPad))
            {
                float deeper = (origin.Z - from.Z) * (to.Z - from.Z);
                if (to.Z != from.Z && deeper > 0f)
                {
                    return true; // heading for the arch plane
                }
                continue; // retreat or parallel: this trigger is no barrier
            }

            float tMin = 0f;
            float tMax = 1f;
            bool hitX = SegmentSlab(from.X, to.X, origin.X - TriggerHalfWidth - GateBodyRadiusPad, origin.X + TriggerHalfWidth + GateBodyRadiusPad, ref tMin, ref tMax);
            bool hitZ = SegmentSlab(from.Z, to.Z, origin.Z - TriggerHalfDepth - GateBodyRadiusPad, origin.Z + TriggerHalfDepth + GateBodyRadiusPad, ref tMin, ref tMax);
            if (hitX && hitZ)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the XZ point lies within the given box around the origin.</summary>
    private static bool InsideBox(Vector3 point, Vector3 origin, float halfWidth, float halfDepth)
        => Mathf.Abs(point.X - origin.X) <= halfWidth && Mathf.Abs(point.Z - origin.Z) <= halfDepth;

    /// <summary>Slab-method step: narrows the parametric interval [0,1] to where the segment lies inside [low, high]; false once the interval empties.</summary>
    private static bool SegmentSlab(float from, float to, float low, float high, ref float tMin, ref float tMax)
    {
        float travel = to - from;
        if (Mathf.Abs(travel) <= GatePathEpsilon)
        {
            return from >= low && from <= high;
        }

        float tEnter = (low - from) / travel;
        float tExit = (high - from) / travel;
        if (tEnter > tExit)
        {
            (tEnter, tExit) = (tExit, tEnter);
        }

        tMin = Mathf.Max(tMin, tEnter);
        tMax = Mathf.Min(tMax, tExit);
        return tMin <= tMax;
    }

    /// <summary>
    /// The current room's live Transition_* triggers, cached per room id (they attach a
    /// couple of frames after LoadRoom — the phantom-swap fix — so an empty cache
    /// re-scans rather than treating "not yet attached" as "no gates"). Freed entries
    /// from a swapped-out room are pruned on read.
    /// </summary>
    private List<Node3D> GateTriggers()
    {
        string roomId = _loader?.CurrentRoomId ?? string.Empty;
        if (roomId != _gateRoomId || _gateTriggers.Count == 0)
        {
            _gateRoomId = roomId;
            _gateTriggers.Clear();
            if (roomId.Length > 0)
            {
                CollectTransitions(FindCurrentRoomNode(), _gateTriggers);
            }
        }

        _gateTriggers.RemoveAll(trigger => !GodotObject.IsInstanceValid(trigger));
        return _gateTriggers;
    }

    /// <summary>Depth-first collection of every RoomTransition under <paramref name="node"/> (triggers hang off arch nodes, one level under the room).</summary>
    private static void CollectTransitions(Node? node, List<Node3D> into)
    {
        if (node is null)
        {
            return;
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is RoomTransition transition)
            {
                into.Add(transition);
            }

            CollectTransitions(child, into);
        }
    }

    // ------------------------------------------------------------------
    // Wiring / input plumbing
    // ------------------------------------------------------------------

    /// <summary>Re-resolves player/health/loader (cached, validity-checked). Missing wiring is a loud FATAL + self-disable.</summary>
    private bool ResolveActors()
    {
        if (!GodotObject.IsInstanceValid(_player))
        {
            _player = GetTree()?.GetFirstNodeInGroup(RoomLoader.PlayerGroup) as Node3D
                ?? GetParent()?.GetNodeOrNull<Node3D>(PlayerSiblingName);
        }

        if (!GodotObject.IsInstanceValid(_health))
        {
            _health = _player?.GetNodeOrNull<PlayerHealth>(HealthChildName);
        }

        _loader ??= GetParent()?.GetNodeOrNull<RoomLoader>(RoomLoaderNodeName);

        if (_player is null || _loader is null)
        {
            GD.PrintErr("pilot: FATAL missing_actor player or RoomLoader not found under Main");
            Disable();
            return false;
        }

        return true;
    }

    /// <summary>Rising edge on <c>debug_autopilot</c> (physical F2) — self-tracked held state, not IsActionJustPressed, so it cannot miss a frame.</summary>
    private void UpdateDebugToggleEdge()
    {
        bool pressed = Input.IsActionPressed(DebugToggleAction);
        if (pressed && !_debugToggleWasPressed)
        {
            PilotEnabled = !PilotEnabled;
        }

        _debugToggleWasPressed = pressed;
    }

    /// <summary>Press/release one action, idempotently — held state is remembered so each action is pressed at most once per hold.</summary>
    private void SetAction(string action, bool pressed)
    {
        // Tripwire (#22): same anonymous-engine-error problem as InputEdge — name + stack it here.
        if (!InputMap.HasAction(action))
        {
            GD.PrintErr($"[SetAction] action '{action}' not in InputMap\n{System.Environment.StackTrace}");
            return;
        }
        if (pressed)
        {
            if (_heldActions.Add(action))
            {
                Input.ActionPress(action);
            }
        }
        else if (_heldActions.Remove(action))
        {
            Input.ActionRelease(action);
        }
    }

    /// <summary>Eight-way steering toward <paramref name="to"/> on the XZ plane (deadzoned per axis so it converges and stops).</summary>
    private void SteerToward(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        delta.Y = 0f;
        ApplySteer(delta);
    }

    /// <summary>Gated approach (Sprint 2.6E): ENGAGE's close phase may not walk the player through an arch either — the target could stand beyond a gate.</summary>
    private void SteerTowardGated(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        delta.Y = 0f;
        GateAgainstTriggers(from, ref delta);
        ApplySteer(delta);
    }

    /// <summary>
    /// Eight-way steering directly away from <paramref name="threat"/> (the damage escape
    /// and the strike cycle's give-ground phase). Gate-aware (Sprint 2.6E): the retreat
    /// vector slides along any Transition trigger footprint it would cross (waived in the
    /// EXIT state), so backing through an always-open entry arch mid-fight is no longer
    /// in the pilot's repertoire.
    /// </summary>
    private void SteerAwayFrom(Vector3 from, Vector3 threat)
    {
        Vector3 delta = from - threat; // along the threat→player line; magnitude irrelevant (sign-deadzoned)
        delta.Y = 0f;
        GateAgainstTriggers(from, ref delta);
        ApplySteer(delta);
    }

    /// <summary>Presses/releases the four move actions from a planar vector (deadzoned per axis so it converges and stops).</summary>
    private void ApplySteer(Vector3 delta)
    {
        SetAction(MoveRightAction, delta.X > MoveAxisDeadzone);
        SetAction(MoveLeftAction, delta.X < -MoveAxisDeadzone);
        SetAction(MoveDownAction, delta.Z > MoveAxisDeadzone); // move_down → +Z
        SetAction(MoveUpAction, delta.Z < -MoveAxisDeadzone);  // move_up → -Z
    }

    /// <summary>Releases the four move actions (attack/dodge cadences release themselves).</summary>
    private void StopSteering()
    {
        SetAction(MoveUpAction, false);
        SetAction(MoveDownAction, false);
        SetAction(MoveLeftAction, false);
        SetAction(MoveRightAction, false);
    }

    /// <summary>
    /// Attack pulse machinery: once pressed (by the strike gate or v1-style), hold for
    /// <see cref="AttackHoldTicks"/>, then release and open
    /// <see cref="AttackCooldownSeconds"/> — SwordHitbox enforces the same window on
    /// the swing itself, so a held press can never machine-gun.
    /// </summary>
    private void TickAttackPulse()
    {
        if (_attackHoldTicksRemaining <= 0)
        {
            return;
        }

        _attackHoldTicksRemaining--;
        if (_attackHoldTicksRemaining == 0)
        {
            SetAction(AttackAction, false);
            _attackCooldownRemaining = AttackCooldownSeconds;
        }
    }

    /// <summary>
    /// Dodge pulse: press for <see cref="DodgeHoldTicks"/> then release, and open the
    /// pilot's own mirror of PlayerController's 0.7 s refractory timer so repeated
    /// calls naturally become no-ops until the controller would actually accept a dodge.
    /// Always steer before calling: PlayerController reads the held move vector at the
    /// rising edge to pick the burst direction.
    /// </summary>
    private void StartDodgePulse()
    {
        TickDodgePulse();
        if (_dodgeHoldTicksRemaining > 0 || _dodgePulseCooldownRemaining > 0f)
        {
            return;
        }

        SetAction(DodgeAction, true);
        _dodgeHoldTicksRemaining = DodgeHoldTicks;
        _dodgePulseCooldownRemaining = DodgeCooldownSeconds;
    }

    private void TickDodgePulse()
    {
        if (_dodgeHoldTicksRemaining <= 0)
        {
            return;
        }

        _dodgeHoldTicksRemaining--;
        if (_dodgeHoldTicksRemaining == 0)
        {
            SetAction(DodgeAction, false);
        }
    }

    private void ReleaseDodgeIfHeld()
    {
        _dodgeHoldTicksRemaining = 0;
        SetAction(DodgeAction, false);
    }

    /// <summary>
    /// Gate-cleared dodge pulse (Sprint 2.6E): the burst carries
    /// <see cref="DodgeBurstDistance"/> in one shot — far past the per-tick walk
    /// lookahead — so it is only armed when the whole burst path stays clear of every
    /// trigger footprint of the current room. With no held move vector the controller
    /// bursts along the body's facing, which a retreat may point straight through an
    /// arch, so a zero hold means no pulse (plain walking always works). In the EXIT
    /// state the gate is waived: crossing an arch there is the whole point.
    /// </summary>
    private void StartDodgePulseIfGateClear()
    {
        if (_state == PilotState.Exit)
        {
            StartDodgePulse();
            return;
        }

        Vector3 held = HeldMoveDirection();
        if (held == Vector3.Zero)
        {
            return; // facing-fallback burst is unverifiable — skip, never guess
        }

        Vector3 from = _player!.GlobalPosition;
        Vector3 to = from + held.Normalized() * DodgeBurstDistance;
        if (PathCrossesTrigger(from, to))
        {
            return;
        }

        StartDodgePulse();
    }

    /// <summary>The planar move vector the controller will read from the held actions at the dodge's rising edge (zero when nothing is held).</summary>
    private Vector3 HeldMoveDirection()
    {
        float x = (_heldActions.Contains(MoveRightAction) ? 1f : 0f)
                - (_heldActions.Contains(MoveLeftAction) ? 1f : 0f);
        float z = (_heldActions.Contains(MoveDownAction) ? 1f : 0f)
                - (_heldActions.Contains(MoveUpAction) ? 1f : 0f);
        return new Vector3(x, 0f, z);
    }

    /// <summary>Releases every action the pilot holds — the only way it ever releases, so a human's own key state is never touched.</summary>
    private void ReleaseAllActions()
    {
        if (_heldActions.Count > 0)
        {
            foreach (string action in _heldActions)
            {
                Input.ActionRelease(action);
            }

            _heldActions.Clear();
        }

        _attackHoldTicksRemaining = 0;
        _dodgeHoldTicksRemaining = 0;
    }

    // ------------------------------------------------------------------
    // State change / logging / fatal outcomes
    // ------------------------------------------------------------------

    /// <summary>FSM transition: releases held inputs, runs enter bookkeeping, logs once (never per tick).</summary>
    private void SetState(PilotState next)
    {
        if (next == _state)
        {
            return;
        }

        ReleaseAllActions(); // every state starts from a clean input slate
        _state = next;

        if (next == PilotState.Engage)
        {
            ResetCycle();
            _attackHoldTicksRemaining = 0;
            _attackCooldownRemaining = 0f;
            _dodgeHoldTicksRemaining = 0;
            _dodgePulseCooldownRemaining = 0f;
            _targetInstanceId = 0; // force a fresh target acquire (fresh weave gates + facing settle)
        }

        GD.Print($"pilot: status={StateName(next)} room={_loader?.CurrentRoomId ?? "?"} hp={HpText}");
    }

    private static string StateName(PilotState state) => state switch
    {
        PilotState.Scan => "scan",
        PilotState.Heart => "heart",
        PilotState.Engage => "engage",
        PilotState.Separate => "separate",
        PilotState.Exit => "exit",
        PilotState.Fragment => "fragment",
        _ => "disabled",
    };

    /// <summary>ENGAGE's logged verb: <c>weave</c> when the target is weave-mode (contact beyond the swing cap or charger-tier — data-driven, no boss/room ids), else <c>engage</c>.</summary>
    private string EngageWord => _engageWeave ? "weave" : "engage";

    private string HpText => _health is null ? "?/?" : $"{_health.Hp}/{_health.HpMax}";

    /// <summary>FATAL: the forward transition did not happen (or could not even be planned).</summary>
    private void FatalAdvanceMissed()
    {
        string expected = _exitExpectedRoom.Length > 0 ? _exitExpectedRoom : "?";
        GD.Print($"pilot: FATAL advance_missed expected={expected} got={_loader?.CurrentRoomId ?? "?"}");
        Disable();
    }

    /// <summary>FATAL: the win fragment could not be collected within the walk timeout.</summary>
    private void FatalFragment()
    {
        GD.Print($"pilot: FATAL fragment_missed room={_loader?.CurrentRoomId ?? "?"}");
        Disable();
    }

    /// <summary>FATAL: the declared heart could not be walked onto within the walk timeout.</summary>
    private void FatalHeart()
    {
        GD.Print($"pilot: FATAL heart_missed room={_loader?.CurrentRoomId ?? "?"} hp={HpText}");
        Disable();
    }

    /// <summary>Self-disables: releases inputs and allows F2 to re-arm the pilot.</summary>
    private void Disable()
    {
        ReleaseAllActions();
        _state = PilotState.Disabled;
        PilotEnabled = false;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        Vector3 delta = a - b;
        delta.Y = 0f;
        return delta.Length();
    }

    /// <summary>True when the player centre stands inside the exit trigger's XZ footprint (strict subset of the Area3D's actual overlap, so a true here guarantees the Area fired).</summary>
    private static bool InsideTriggerFootprint(Vector3 player, Vector3 trigger) =>
        Mathf.Abs(player.X - trigger.X) <= TriggerHalfWidth
        && Mathf.Abs(player.Z - trigger.Z) <= TriggerHalfDepth;
}
