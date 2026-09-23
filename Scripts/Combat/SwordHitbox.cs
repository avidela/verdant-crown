using Godot;
using System.Collections.Generic;

namespace VerdantCrown.Combat;

/// <summary>
/// Area3D sword swing for the top-down action combat. The player controller calls
/// <see cref="Swing"/> once per attack press; a swing opens a brief damage window
/// during which every overlapping <see cref="IDamageable"/> body or area is damaged
/// exactly once. A cooldown gates how soon the next swing may start.
///
/// Collision mapping (set in Player.tscn): collision layer 0 (the sword reports
/// nothing), collision mask 2 (enemy bodies).
/// </summary>
public partial class SwordHitbox : Area3D
{
    // --- Swing tuning -------------------------------------------------------
    private const int SwingDamage = 1;               // flat damage dealt per successful swing
    private const double ActiveWindowSeconds = 0.15; // the hitbox is live for 150 ms
    private const double SwingCooldownSeconds = 0.4; // full cycle between consecutive swings

    // --- Procedural swing visual (grey-box fallback; Sprint 3c pitches the hero's arm) ---
    private const float SwingArcStartDeg = 80f;    // tip raised overhead at swing start
    private const float SwingArcEndDeg = -30f;     // follow-through below guard
    private const double SwingArcSeconds = 0.25;   // slash crosses the 0.15s damage window
    private const double SwingReturnSeconds = 0.12; // ease back to guard after follow-through
    private const string VisualNodeName = "MeshInstance3D";

    /// <summary>
    /// hero.glb guarantees this node name (shoulder-pivot arm with the sword baked in,
    /// authored hanging down = rotation 0 rest pose). Resolved by NAME, never by full path.
    /// </summary>
    private const string SwingArmNodeName = "SwordArm";

    private readonly HashSet<ulong> _hitThisSwing = new();
    private double _activeRemaining;
    private double _cooldownRemaining;
    private double _visualRemaining;
    private MeshInstance3D? _visual;
    private Node3D? _swingArm;

    public override void _Ready()
    {
        // Fallback visual: the bare grey-box sword mesh child (null-meshed in Player.tscn,
        // still present so bare-sword scenes keep rotating it).
        _visual = GetNodeOrNull<MeshInstance3D>(VisualNodeName);

        // Sprint 3c: resolve the hero rig's arm once — walk up the ancestor chain and search
        // each subtree by NAME (the export guarantees it), so no full path is hardcoded.
        _swingArm = ResolveSwingArm();
        if (_swingArm is not null)
        {
            // TEMP DEBUG (Sprint 3c hero integration): confirms arm resolution in headless smoke logs.
            GD.Print($"{Name}: TEMP DEBUG swing arm resolved → '{_swingArm.GetPath()}'.");
        }
        else if (_visual is null)
        {
            GD.PrintErr($"{Name}: no '{SwingArmNodeName}' found above and child '{VisualNodeName}' " +
                        "missing — swings will be invisible.");
        }
        else
        {
            GD.Print($"{Name}: '{SwingArmNodeName}' not found above — falling back to the local " +
                     "sword mesh visual.");
        }
    }

    /// <summary>
    /// Walks up from the sword to its parent (the facing-rotation node) and beyond, running a
    /// recursive NAME search in each ancestor's descendants. Returns null on bare-sword scenes
    /// (Whitebox fallback) — callers keep the old local-mesh behaviour then.
    /// </summary>
    private Node3D? ResolveSwingArm()
    {
        for (Node? ancestor = GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
        {
            // owned: false — the imported rig's nodes are not owned by this scene.
            if (ancestor.FindChild(SwingArmNodeName, recursive: true, owned: false) is Node3D arm)
            {
                return arm;
            }
        }

        return null;
    }

    /// <summary>True while this swing's damage window is open.</summary>
    public bool IsActive => _activeRemaining > 0.0;

    /// <summary>True when a new swing may be started.</summary>
    public bool CanSwing => _cooldownRemaining <= 0.0 && !IsActive;

    /// <summary>
    /// Starts a swing. Ignored while a swing is active or the cooldown is still running,
    /// which enforces one swing per attack press.
    /// </summary>
    public void Swing()
    {
        if (!CanSwing)
        {
            GD.Print("[fx] swing ignored (cooldown/active)");
            return;
        }

        GD.Print("[fx] swing EXECUTED");

        _hitThisSwing.Clear();
        _activeRemaining = ActiveWindowSeconds;
        _cooldownRemaining = SwingCooldownSeconds;
        _visualRemaining = SwingArcSeconds + SwingReturnSeconds;
    }

    public override void _PhysicsProcess(double delta)
    {
        _cooldownRemaining = Mathf.Max(0.0, _cooldownRemaining - delta);
        TickVisual(delta);

        if (_activeRemaining <= 0.0)
        {
            return;
        }

        _activeRemaining = Mathf.Max(0.0, _activeRemaining - delta);
        DamageOverlaps();
    }

    /// <summary>
    /// Drives the slash: raise → arc through the damage window → ease back to guard.
    /// Sprint 3c: pitches the hero rig's <c>SwordArm</c> (rest = 0 = authored hang pose) with
    /// the same arc constants; falls back to the local grey-box mesh child when the arm is
    /// absent. Only the target's rotation is touched — never the Area3D — so the hitbox stays
    /// a stable forward box and combat distances do not move.
    /// </summary>
    private void TickVisual(double delta)
    {
        Node3D? target = _swingArm ?? _visual;
        if (target is null)
        {
            return;
        }

        if (_visualRemaining <= 0.0)
        {
            target.Rotation = Vector3.Zero; // rest pose: arm hangs as authored / mesh squares up
            return;
        }

        _visualRemaining = Mathf.Max(0.0, _visualRemaining - delta);
        if (_visualRemaining > SwingReturnSeconds)
        {
            double arcElapsed = SwingArcSeconds - (_visualRemaining - SwingReturnSeconds);
            float u = (float)(arcElapsed / SwingArcSeconds);
            float deg = Mathf.Lerp(SwingArcStartDeg, SwingArcEndDeg, u);
            target.Rotation = new Vector3(Mathf.DegToRad(deg), 0f, 0f);
        }
        else
        {
            float u = (float)(1.0 - _visualRemaining / SwingReturnSeconds);
            float deg = Mathf.Lerp(SwingArcEndDeg, 0f, u);
            target.Rotation = new Vector3(Mathf.DegToRad(deg), 0f, 0f);
        }
    }

    /// <summary>Damages every not-yet-hit <see cref="IDamageable"/> currently overlapping the sword.</summary>
    private void DamageOverlaps()
    {
        foreach (CollisionObject3D body in GetOverlappingBodies())
        {
            TryDamage(body);
        }

        foreach (Area3D area in GetOverlappingAreas())
        {
            if (area != this)
            {
                TryDamage(area);
            }
        }
    }

    private void TryDamage(Node node)
    {
        if (!IsActive)
        {
            return; // window closed earlier this tick — no damage outside the swing
        }

        IDamageable? target = FindDamageable(node);
        if (target is null)
        {
            return;
        }

        // One hit per target per swing.
        ulong key = target is Node targetNode ? targetNode.GetInstanceId() : (ulong)target.GetHashCode();
        if (!_hitThisSwing.Add(key))
        {
            return;
        }

        target.TakeDamage(SwingDamage);
    }

    /// <summary>
    /// Finds the damageable on the detected node itself, or — for body/area hosts like
    /// Whitebox's enemy whose script sits on a child node — on its direct children.
    /// </summary>
    private static IDamageable? FindDamageable(Node node)
    {
        if (node is IDamageable direct)
        {
            return direct;
        }

        foreach (Node child in node.GetChildren())
        {
            if (child is IDamageable childDamageable)
            {
                return childDamageable;
            }
        }

        return null;
    }
}
