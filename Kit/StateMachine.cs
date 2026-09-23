using System;
using System.Collections.Generic;

namespace Kit;

/// <summary>
/// A single state that can be driven by a <see cref="StateMachine{TContext}"/>.
/// Implementations should be small and hold no wiring to the machine itself.
/// </summary>
/// <typeparam name="TContext">The context (owner) this state operates on.</typeparam>
public interface IState<TContext>
{
    /// <summary>
    /// Seconds spent in this state. Owned by the state machine: it is reset to zero on
    /// Enter and Exit, and incremented by <see cref="StateMachine{TContext}.Update"/>
    /// before the state's own <c>Update</c> runs. Implement as an auto-property and do
    /// not mutate it yourself.
    /// </summary>
    float TimeInState { get; set; }

    /// <summary>Called when the machine transitions into this state.</summary>
    void Enter(TContext context);

    /// <summary>Called every tick while this state is active; <paramref name="delta"/> is in seconds.</summary>
    void Update(TContext context, float delta);

    /// <summary>Called when the machine transitions out of this state.</summary>
    void Exit(TContext context);
}

/// <summary>
/// Minimal finite state machine with named states and named transitions.
/// States are registered by name with <see cref="Add"/>; <see cref="Move"/> performs the
/// transition and silently ignores names that were never registered instead of throwing,
/// so unknown runtime triggers are harmless. This type is intentionally game-agnostic.
/// </summary>
/// <typeparam name="TContext">The context passed to every state callback.</typeparam>
public sealed class StateMachine<TContext>
{
    private readonly Dictionary<string, IState<TContext>> _states = new(StringComparer.Ordinal);
    private readonly TContext _context;

    /// <summary>Creates a machine that passes <paramref name="context"/> to every state callback.</summary>
    public StateMachine(TContext context)
    {
        _context = context;
    }

    /// <summary>The active state, or <see langword="null"/> before the first <see cref="Move"/>.</summary>
    public IState<TContext>? Current { get; private set; }

    /// <summary>Name the active state was moved to, or <see langword="null"/> before the first <see cref="Move"/>.</summary>
    public string? CurrentName { get; private set; }

    /// <summary>Seconds spent in the active state; 0 when no state is active.</summary>
    public float TimeInState => Current?.TimeInState ?? 0f;

    /// <summary>Number of registered states.</summary>
    public int Count => _states.Count;

    /// <summary>
    /// Registers <paramref name="state"/> under <paramref name="name"/>.
    /// Throws on null state, null/empty name, or duplicate name — a duplicate registration
    /// is a programming error, unlike an unknown name at move time.
    /// </summary>
    public void Add(string name, IState<TContext> state)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("State name must be non-empty.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(state);
        _states.Add(name, state);
    }

    /// <summary>Whether a state is registered under <paramref name="name"/>.</summary>
    public bool Has(string name) => name is not null && _states.ContainsKey(name);

    /// <summary>
    /// Transitions to the state registered under <paramref name="name"/>: exits the current
    /// state (resetting its <see cref="IState{TContext}.TimeInState"/>), then enters the target.
    /// Unknown (or null) names are ignored and return <see langword="false"/>; moving to the
    /// already-current name is a no-op and returns <see langword="true"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the named state exists and is current afterwards.</returns>
    public bool Move(string? name)
    {
        if (name is null || !_states.TryGetValue(name, out var next))
        {
            return false; // Unknown transition name: ignore instead of crashing.
        }

        if (string.Equals(name, CurrentName, StringComparison.Ordinal))
        {
            return true; // Already in this state.
        }

        if (Current is not null)
        {
            Current.Exit(_context);
            Current.TimeInState = 0f;
        }

        Current = next;
        CurrentName = name;
        next.TimeInState = 0f;
        next.Enter(_context);
        return true;
    }

    /// <summary>
    /// Advances the active state by <paramref name="delta"/> seconds: increments
    /// <see cref="IState{TContext}.TimeInState"/>, then calls the state's Update.
    /// No-op when no state is active. Call this from your node's process callback.
    /// </summary>
    public void Update(float delta)
    {
        if (Current is null)
        {
            return;
        }

        Current.TimeInState += delta;
        Current.Update(_context, delta);
    }
}
