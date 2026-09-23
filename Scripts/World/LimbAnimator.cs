using Godot;
using System.Collections.Generic;

namespace VerdantCrown.World;

/// <summary>
/// Sprint 3e — procedural walk cycle for the hero rig, driven ONLY by measured
/// horizontal speed (no animation assets). Owns the planar odometer (position delta
/// is truth), the distance-based stride phase, and every write to the rig's leg and
/// torso joints. It never writes the player's position, and it never touches
/// <c>SwordArm</c> — SwordHitbox owns that joint.
///
/// Rules (see <see cref="Tick"/>):
/// <list type="bullet">
/// <item>Legs pitch in antiphase; amplitude scales 0 → full with measured speed.</item>
/// <item>Stride phase advances by metres travelled / stride length, so it syncs at
/// any speed and freezes at a standstill; the phase persists across ticks.</item>
/// <item>Stopping blends the last pose back to rest over ~0.15 s (wrap-safe).</item>
/// <item>Dodge burst and an active sword swing HOLD the pose (freeze — documented
/// choice: following the 12 m/s dodge spike would demand a 15 Hz stride).</item>
/// </list>
///
/// Cost: three struct writes and two <c>Sin</c> calls per physics tick — no
/// allocations, no LINQ, no <c>GetNode</c> after <see cref="Resolve"/>.
/// </summary>
public sealed class LimbAnimator
{
    // --- Rig node names (guaranteed by the hero.glb export) ------------------
    private const string TorsoNodeName = "Torso";
    private const string LegLeftNodeName = "Leg_L";
    private const string LegRightNodeName = "Leg_R";

    // --- Gait tuning ---------------------------------------------------------
    private const float StrideLengthMeters = 0.8f; // metres per full gait cycle (both legs)
    private const float MaxLegSwingDegrees = 32f;  // full stride at reference speed (spec: ±30–35°)
    private const float TorsoBobMeters = 0.04f;    // ±4 cm at 2× leg frequency (spec: 0.03–0.05 m)
    private const float MoveBlendSeconds = 0.15f;  // blend into/out of the stride
    private const float MoveSpeedThresholdMeters = 0.1f; // below this the player counts as stood still

    /// <summary>Full-stride reference speed — PlayerController.MoveSpeed, passed at construction.</summary>
    private readonly float _referenceSpeed;

    private readonly float _maxSwingRadians = Mathf.DegToRad(MaxLegSwingDegrees);

    private Node3D? _torso;
    private Node3D? _legLeft;
    private Node3D? _legRight;
    private Vector3 _torsoRestPosition;
    private Vector3 _legLeftRestRotation;
    private Vector3 _legRightRestRotation;

    private Vector3 _lastRootPosition;
    private bool _hasLastPosition;
    private float _phase;     // gait cycles — persisted across ticks
    private float _blend;     // 0 = rest pose, 1 = full stride
    private float _swingHold; // last animated leg swing (frozen while not moving)
    private float _bobHold;   // last animated torso bob (frozen while not moving)
    private bool _resolved;

    public LimbAnimator(float referenceSpeed)
    {
        _referenceSpeed = referenceSpeed;
    }

    /// <summary>
    /// Resolves the limb joints ONCE by recursive NAME search under the facing node
    /// (same pattern as SwordHitbox: <c>owned: false</c> because the imported rig's
    /// nodes are not owned by Player.tscn). On success prints a single TEMP DEBUG
    /// line listing the found node names; on any missing limb it logs once and
    /// disables animation entirely (rest pose, no errors) rather than animating a
    /// half-rig.
    /// </summary>
    public void Resolve(Node3D searchRoot)
    {
        _torso = FindLimb(searchRoot, TorsoNodeName);
        _legLeft = FindLimb(searchRoot, LegLeftNodeName);
        _legRight = FindLimb(searchRoot, LegRightNodeName);

        if (_torso is null || _legLeft is null || _legRight is null)
        {
            List<string> missing = new(3);
            if (_torso is null)
            {
                missing.Add(TorsoNodeName);
            }

            if (_legLeft is null)
            {
                missing.Add(LegLeftNodeName);
            }

            if (_legRight is null)
            {
                missing.Add(LegRightNodeName);
            }

            GD.PrintErr($"{nameof(LimbAnimator)}: limb node(s) {string.Join(", ", missing)} not found " +
                        $"under '{searchRoot.Name}' — walk animation disabled (rest pose kept).");
            _resolved = false;
            return;
        }

        _torsoRestPosition = _torso.Position;
        _legLeftRestRotation = _legLeft.Rotation;
        _legRightRestRotation = _legRight.Rotation;
        _resolved = true;

        // TEMP DEBUG (Sprint 3e walk cycle): exactly one line, listing resolved names.
        GD.Print($"{nameof(LimbAnimator)}: TEMP DEBUG limbs resolved → " +
                 $"'{_torso.Name}', '{_legLeft.Name}', '{_legRight.Name}' under '{searchRoot.GetPath()}'.");
    }

    /// <summary>
    /// Advances the odometer and the gait, then writes the joints. Call exactly once
    /// per physics tick, AFTER <c>MoveAndSlide</c>, with the player root's world
    /// position — the planar position delta is the source of truth for speed.
    ///
    /// Freeze rules (pose, phase and blend all held exactly; joints not written):
    /// <list type="bullet">
    /// <item><b>Dodge burst</b> — spec's "freeze" option. Following the 12 m/s spike
    /// would ask for a 15 Hz stride (12 / 0.8 m); a held pose for the 0.15 s burst
    /// reads as a lunge and resumes from the same phase with no pop.</item>
    /// <item><b>Sword mid-swing</b> (<c>SwordHitbox.IsActive</c>, the 0.15 s damage
    /// window) — the whole body holds rather than animating while SwordHitbox drives
    /// SwordArm; this animator never writes that joint.</item>
    /// </list>
    /// Not-moving is NOT a freeze: <c>_blend</c> walks to 0 over ~0.15 s, pulling the
    /// held pose back to the authored rest pose (LerpAngle wraps the ±180 boundary).
    /// </summary>
    public void Tick(double delta, Vector3 rootPosition, bool dodgeBurst, bool swordSwinging)
    {
        if (delta <= 0.0)
        {
            return;
        }

        // --- Odometer: planar position delta, integrated every tick — even while
        // frozen, so the first post-freeze tick sees a fresh delta, not a stale one.
        // Y is dropped: falling is not walking. ---
        Vector3 planarDelta = rootPosition - _lastRootPosition;
        planarDelta.Y = 0f;
        float horizontalSpeed = _hasLastPosition ? planarDelta.Length() / (float)delta : 0f;
        _lastRootPosition = rootPosition;
        _hasLastPosition = true;

        if (!_resolved)
        {
            return; // fallback from Resolve: no animation, no errors
        }

        if (dodgeBurst || swordSwinging)
        {
            return; // freeze: hold the current pose, phase and blend — see rules above
        }

        bool moving = horizontalSpeed >= MoveSpeedThresholdMeters;
        _blend = Mathf.MoveToward(_blend, moving ? 1f : 0f, (float)delta / MoveBlendSeconds);

        if (moving)
        {
            // Distance-based phase (NOT time-based): cycles advanced = metres / stride.
            // At 5 m/s with a 0.8 m stride the phase runs 6.25 cycles/s; at 0 m/s it
            // freezes by construction. Persisted in _phase across ticks.
            _phase += horizontalSpeed / StrideLengthMeters * (float)delta;
            _phase -= Mathf.Floor(_phase); // keep float precision over long sessions

            // Amplitude scales with measured speed: 0 at standstill → full at reference.
            float amplitudeScale = Mathf.Clamp(horizontalSpeed / _referenceSpeed, 0f, 1f);
            _swingHold = _maxSwingRadians * amplitudeScale * Mathf.Sin(_phase * Mathf.Tau);
            _bobHold = TorsoBobMeters * amplitudeScale * Mathf.Sin(_phase * Mathf.Tau * 2f);
        }

        // Not moving: _swingHold/_bobHold keep the last animated pose while _blend
        // lerps it to rest over ~0.15 s. Legs antiphase (sign-flipped), bob at 2×.
        SetLegPitch(_legLeft!, _legLeftRestRotation, _swingHold, _blend);
        SetLegPitch(_legRight!, _legRightRestRotation, -_swingHold, _blend);
        _torso!.Position = _torsoRestPosition + new Vector3(0f, _bobHold * _blend, 0f);
    }

    /// <summary>Writes one leg's pitch, blended from its authored rest pose.</summary>
    private static void SetLegPitch(Node3D leg, Vector3 restRotation, float pitchOffset, float blend)
    {
        // LerpAngle wraps across ±180, so the return-to-rest blend stays correct even
        // if an authored rest pitch ever sits near the boundary.
        float pitch = Mathf.LerpAngle(restRotation.X, restRotation.X + pitchOffset, blend);
        leg.Rotation = new Vector3(pitch, restRotation.Y, restRotation.Z);
    }

    private static Node3D? FindLimb(Node3D searchRoot, string limbName) =>
        searchRoot.FindChild(limbName, recursive: true, owned: false) as Node3D;
}
