using System;
using Godot;

namespace VerdantCrown.Combat;

/// <summary>
/// Sprint 3f — whole-instance enemy motion (bounce / bob / sway). Attached to the visual
/// slot of <c>EnemyBody.tscn</c> (the <c>MeshInstance3D</c> child that <see cref="EnemyBrain"/>
/// swaps the GLB model under). Animates ONLY this node's local transform — y-offset, yaw,
/// scale — never mesh geometry and never the physics root, so <c>MoveAndSlide</c> keeps
/// owning the body transform. Measured-cheap crowd-safe pattern: fields only, zero
/// per-frame allocations, zero Linq, zero GetNode, behaviour re-read as a plain property.
/// <para>Style is keyed off <see cref="EnemyBrain.Behavior"/> (enemies.json, phase-swappable
/// via <see cref="EnemyBrain.SetPhaseBehavior"/>); re-resolved only when the string changes:</para>
/// <list type="bullet">
/// <item><description><c>"wander"</c> → HOP+SQUASH — distance-phased hops (0.45 m per hop):
/// lift <c>max(0, sin φ)·0.10 m</c>; <c>scaleY = 1 + (0.10·max(0,sin φ) − 0.15·max(0,−sin φ))·t</c>
/// (air stretch / ground flatten), <c>scaleXZ = 1/√scaleY</c> (volume-ish preserve); when
/// stopped, subtle breathing <c>scaleY = 1 + 0.02·sin(time·2)</c>.</description></item>
/// <item><description><c>"charge_player"</c> → MARCH — lift-only Y-bob <c>max(0, sin φ)·0.04 m</c>
/// at 2× the wander frequency (0.225 m wavelength); no scale, no rotation (readability + safety).</description></item>
/// <item><description><c>"boss_swing…"</c> (prefix — covers phase-2 <c>"boss_swing_aggressive"</c>)
/// → SWAY — slow yaw <c>±2°</c> + <c>scaleY 1 ± 0.015</c> breath at 0.4 Hz, ALWAYS on, never damped.</description></item>
/// <item><description>anything else (or no brain) → rest at the captured base transform.</description></item>
/// </list>
/// Speed damping: amplitude target <c>t = clamp(planarSpeed / 4 m/s, 0, 1)</c>, smoothed toward
/// rest at 8 /s so a sudden stop settles instead of snapping; only the boss sway ignores it.
/// <para>Base-transform capture: scene-authored local position / rotation / scale are read once
/// in <c>_Ready</c> (child ready runs before the parent's, i.e. before EnemyBrain's GLB swap —
/// and that swap only touches <c>slot.Mesh</c> plus adds a child, never the slot transform),
/// then every frame composes absolute values from that base, so capture order is safe either way.</para>
/// <para>Base-class note (deviation from a Node3D design): the script attaches to a native
/// <c>MeshInstance3D</c> slot, so the class derives <c>MeshInstance3D</c>. Godot's typed
/// <c>GetNodeOrNull&lt;T&gt;</c> is a plain CLR <c>as</c> cast on the managed script instance —
/// a <c>Node3D</c>-based script class on a <c>MeshInstance3D</c> node would make
/// <see cref="EnemyBrain"/>'s slot lookup return null and silently break the art swap.
/// <c>MeshInstance3D</c> is still-a-Node3D for every member used here.</para>
/// </summary>
public partial class EnemyMotion : MeshInstance3D
{
    // --- Behavior keys (enemies.json values — conversion boundary only) -----
    private const string BehaviorWander = "wander";
    private const string BehaviorCharge = "charge_player";
    private const string BehaviorBossPrefix = "boss_swing"; // + "_aggressive" in boss phase 2

    // --- Speed damping ------------------------------------------------------
    private const float SpeedNormalizerMps = 4.0f;  // t = clamp(speed / 4, 0, 1)
    private const float SpeedDampPerSecond = 8.0f;  // amplitude lerp toward rest
    private const float PlanarSpeedEpsilon = 0.01f; // below this: stopped (phase freezes)

    // --- wander: HOP + SQUASH ----------------------------------------------
    private const float HopLengthMeters = 0.45f;      // planar distance per hop (distance-phase)
    private const float HopHeightMeters = 0.10f;      // max airborne lift
    private const float SquashStretchAir = 0.10f;     // scaleY = 1 + 0.10·max(0, sin φ)
    private const float SquashFlattenGround = 0.15f;  // scaleY = 1 − 0.15·max(0,−sin φ)
    private const float IdleBreathAmplitude = 0.02f;  // stopped: scaleY = 1 + 0.02·sin(time·2)
    private const float IdleBreathAngularSpeed = 2f;  // rad/s (literal sin(time·2))

    // --- charge_player: MARCH -----------------------------------------------
    private const float MarchBobMeters = 0.04f;                 // lift-only Y bob
    private const float MarchHopLengthMeters = HopLengthMeters * 0.5f; // 2× wander frequency

    // --- boss_swing: SWAY ----------------------------------------------------
    private const float BossYawRadians = 2f * Mathf.Pi / 180f; // ±2° slow yaw
    private const float BossBreathScale = 0.015f;               // scaleY 1 ± 0.015
    private const float BossSwayHz = 0.4f;                      // always on, undamped
    private const float BossSwayAngularSpeed = BossSwayHz * 2f * Mathf.Pi;

    private enum MotionStyle
    {
        None,  // unknown / unbound behavior → base transform
        Hop,   // wander
        March, // charge_player
        Sway,  // boss_swing*
    }

    private EnemyBrain? _brain;
    private Vector3 _basePosition;  // scene-authored slot transform, captured once
    private Vector3 _baseRotation;
    private Vector3 _baseScale;
    private MotionStyle _style;
    private string? _resolvedBehavior; // cached data string — no per-frame string churn
    private float _phase;   // distance-phased hop / march oscillator (radians)
    private float _elapsed; // wall time for idle breath + boss sway
    private float _amplitude; // smoothed speed-damp 0..1 → rest when stopped
    private bool _loggedFirstFrame; // TEMP DEBUG one-shot

    public override void _Ready()
    {
        // Parent's script is attached at instantiation, so the typed cast resolves even
        // though EnemyBrain._Ready (which binds Behavior) has not run yet at this point.
        _brain = GetParent() as EnemyBrain;
        _basePosition = Position;
        _baseRotation = Rotation;
        _baseScale = Scale;
        // TEMP DEBUG (Sprint 3f smoke): child _Ready precedes parent _Ready, so the behavior
        // name is not bound yet — it is printed on the first _Process below.
        GD.Print($"[Sprint3f] EnemyMotion ready on '{_brain?.Name.ToString() ?? Name}' " +
                 $"(brain={(_brain is null ? "MISSING" : "ok")}, base y={_basePosition.Y:F2}).");
    }

    public override void _Process(double delta)
    {
        if (_brain is null)
        {
            return;
        }

        float dt = (float)delta;
        _elapsed += dt;

        string behavior = _brain.Behavior;
        if (!string.Equals(behavior, _resolvedBehavior, StringComparison.Ordinal))
        {
            _resolvedBehavior = behavior;
            _style = ResolveStyle(behavior);
        }

        Vector3 velocity = _brain.Velocity; // CharacterBody3D public property — no EnemyBrain edit
        float speed = Mathf.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        float target = Mathf.Clamp(speed / SpeedNormalizerMps, 0f, 1f);
        _amplitude = Mathf.Lerp(_amplitude, target, Mathf.Min(1f, dt * SpeedDampPerSecond));

        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            // TEMP DEBUG (Sprint 3f smoke): first frame — behavior is bound by now.
            GD.Print($"[Sprint3f] '{_brain.Name}' motion behavior='{behavior}' style={_style} " +
                     $"speed={speed:F2} m/s damp={_amplitude:F2}.");
        }

        float y = _basePosition.Y;
        float yaw = _baseRotation.Y;
        float scaleY = 1f;
        float scaleXz = 1f;

        switch (_style)
        {
            case MotionStyle.Hop:
            {
                if (speed > PlanarSpeedEpsilon)
                {
                    _phase += speed / HopLengthMeters * Mathf.Tau * dt; // hops/s = speed / 0.45
                }

                float sin = Mathf.Sin(_phase);
                float air = Mathf.Max(0f, sin);
                float ground = Mathf.Max(0f, -sin);
                y += air * HopHeightMeters * _amplitude;
                scaleY = 1f
                    + (SquashStretchAir * air - SquashFlattenGround * ground) * _amplitude
                    + IdleBreathAmplitude * (1f - _amplitude) * Mathf.Sin(_elapsed * IdleBreathAngularSpeed);
                scaleXz = 1f / Mathf.Sqrt(scaleY); // volume-ish preserve (scaleY ≥ 0.85)
                break;
            }

            case MotionStyle.March:
            {
                if (speed > PlanarSpeedEpsilon)
                {
                    _phase += speed / MarchHopLengthMeters * Mathf.Tau * dt; // 2× wander freq
                }

                y += Mathf.Max(0f, Mathf.Sin(_phase)) * MarchBobMeters * _amplitude;
                break; // Y-bob only — scale and rotation stay at base
            }

            case MotionStyle.Sway:
            {
                float sway = Mathf.Sin(_elapsed * BossSwayAngularSpeed); // 0.4 Hz, always on
                yaw += sway * BossYawRadians;
                scaleY = 1f + sway * BossBreathScale;
                break; // no speed damping by design
            }

            case MotionStyle.None:
            default:
                break; // rest at the captured base transform
        }

        Position = new Vector3(_basePosition.X, y, _basePosition.Z);
        Rotation = new Vector3(_baseRotation.X, yaw, _baseRotation.Z);
        Scale = new Vector3(_baseScale.X * scaleXz, _baseScale.Y * scaleY, _baseScale.Z * scaleXz);
    }

    /// <summary>
    /// Converts the enemies.json behavior string to a motion style — the single
    /// stringly-typed boundary; everything downstream uses the enum. The boss prefix match
    /// keeps the sway across phase swaps (<c>"boss_swing"</c> → <c>"boss_swing_aggressive"</c>).
    /// </summary>
    private static MotionStyle ResolveStyle(string? behavior)
    {
        if (string.IsNullOrEmpty(behavior))
        {
            return MotionStyle.None;
        }

        if (behavior == BehaviorWander)
        {
            return MotionStyle.Hop;
        }

        if (behavior == BehaviorCharge)
        {
            return MotionStyle.March;
        }

        return behavior.StartsWith(BehaviorBossPrefix, StringComparison.Ordinal)
            ? MotionStyle.Sway
            : MotionStyle.None;
    }
}
