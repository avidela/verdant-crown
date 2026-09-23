using System;
using Godot;

namespace VerdantCrown.Core;

/// <summary>
/// Global event relay (autoload). Systems never reference each other; they publish through
/// the static <c>Raise*</c> methods and subscribe either way:
/// <list type="bullet">
/// <item>C# code: static events — <c>EventBus.GamePaused += handler;</c></item>
/// <item>Scenes/nodes: Godot signals — connect to <c>SignalName.GamePausedSignal</c>.</item>
/// </list>
/// Every <c>Raise*</c> method fires both styles. Subscribers are responsible for
/// unsubscribing (<c>-=</c> / disconnecting) when they leave the tree.
/// </summary>
public partial class EventBus : Node
{
    /// <summary>The autoload instance; null until the autoload has entered the tree.</summary>
    public static EventBus? Instance { get; private set; }

    // NOTE: The Godot signals carry a "Signal" suffix because the C# source generator
    // declares an instance event per [Signal] delegate using the exact delegate name minus
    // "EventHandler" — a Godot signal named "GamePaused" would generate an instance event
    // "GamePaused" that collides (CS0102) with the static event of the same name.

    /// <summary>Godot signal: player-facing HUD data changed; UI should refresh itself.</summary>
    [Signal]
    public delegate void PlayerHudChangedSignalEventHandler();

    /// <summary>Godot signal: the game paused or resumed.</summary>
    [Signal]
    public delegate void GamePausedSignalEventHandler(bool paused);

    /// <summary>Player-facing HUD data changed; UI should re-read state and refresh. No payload by design.</summary>
    public static event Action? PlayerHudChanged;

    /// <summary>Game paused or resumed; argument is true while paused.</summary>
    public static event Action<bool>? GamePaused;

    public override void _Ready()
    {
        // Keep relaying events while the scene tree is paused.
        ProcessMode = ProcessModeEnum.Always;
        Instance = this;
    }

    /// <summary>Publishes that player-facing HUD data changed. Safe to call before the autoload is ready.</summary>
    public static void RaisePlayerHudChanged()
    {
        PlayerHudChanged?.Invoke();
        Instance?.EmitSignalPlayerHudChangedSignal();
    }

    /// <summary>Publishes a pause-state change. Safe to call before the autoload is ready.</summary>
    public static void RaiseGamePaused(bool paused)
    {
        GamePaused?.Invoke(paused);
        Instance?.EmitSignalGamePausedSignal(paused);
    }
}
