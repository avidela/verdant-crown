using System;
using Godot;

namespace Kit;

/// <summary>
/// Rising/falling edge tracker for a single named <see cref="Input"/> action.
///
/// Godot's remote test injection (and any caller using <c>Input.ActionPress</c>) only
/// sets the HELD state — it dispatches no <see cref="InputEvent"/>, so event callbacks
/// never see it. This type polls <see cref="Input.IsActionPressed"/> itself and
/// remembers the previous held state, turning held-state input into frame-accurate
/// edges that work identically for human keyboard input and remote injection.
///
/// Usage: construct once (per action), call <see cref="Update"/> exactly once at the
/// start of your per-frame callback (e.g. <c>_PhysicsProcess</c>), then read
/// <see cref="Rising"/>, <see cref="Held"/> and <see cref="Falling"/> for that frame.
/// The edges stay latched until the next <see cref="Update"/>, so evaluate them in
/// the same frame you called <see cref="Update"/>.
///
/// This type is intentionally game-agnostic: BCL + Godot only, no game knowledge.
/// </summary>
public sealed class InputEdge
{
    private readonly string _action;
    private bool _isHeld;
    private bool _wasHeld;

    /// <summary>
    /// Starts tracking <paramref name="action"/>. The initial held state is sampled
    /// immediately, so an action already held at construction produces no spurious
    /// rising edge on the first <see cref="Update"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="action"/> is null or empty.</exception>
    public InputEdge(string action)
    {
        if (string.IsNullOrEmpty(action))
        {
            throw new ArgumentException("Action name must be non-empty.", nameof(action));
        }
        if (!InputMap.HasAction(action))
        {
            // Tripwire (#22): an undefined name would otherwise surface only as the engine's
            // anonymous `action "..." doesn't exist` error — name it + stack it here.
            GD.PrintErr($"[InputEdge] action '{action}' not in InputMap\n{System.Environment.StackTrace}");
        }

        _action = action;
        _isHeld = Input.IsActionPressed(action);
        _wasHeld = _isHeld;
    }

    /// <summary>The action name this edge tracker watches.</summary>
    public string Action => _action;

    /// <summary>True while the action is held as of the last <see cref="Update"/>.</summary>
    public bool Held => _isHeld;

    /// <summary>
    /// True only on the frame where the action went from released to held
    /// (evaluated by the last <see cref="Update"/>).
    /// </summary>
    public bool Rising => _isHeld && !_wasHeld;

    /// <summary>
    /// True only on the frame where the action went from held to released
    /// (evaluated by the last <see cref="Update"/>).
    /// </summary>
    public bool Falling => !_isHeld && _wasHeld;

    /// <summary>
    /// Samples <see cref="Input.IsActionPressed"/> for the action and shifts the
    /// previous held state, so <see cref="Rising"/>/<see cref="Falling"/> reflect
    /// this frame's transition. Call once per frame, before reading the edges.
    /// </summary>
    public void Update()
    {
        _wasHeld = _isHeld;
        _isHeld = Input.IsActionPressed(_action);
    }
}
