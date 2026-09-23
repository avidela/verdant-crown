using Godot;

namespace VerdantCrown.Core;

/// <summary>Global game state singleton (autoload). Boot → Menu → Playing → Paused, plus Victory (crown collected) and GameOver (player HP depleted).</summary>
public partial class GameManager : Node
{
    public static GameManager Instance { get; private set; } = null!;

    /// <summary>
    /// Game states: Boot/Menu/Playing/Paused drive the shell; Victory fires when the Crown
    /// Fragment is collected with the boss defeated, GameOver when PlayerHealth hits zero
    /// (both names come from progression.json result_state strings).
    /// </summary>
    public enum GameState { Boot, Menu, Playing, Paused, Victory, GameOver }

    public GameState State { get; private set; } = GameState.Boot;

    [Signal] public delegate void StateChangedEventHandler(int oldState, int newState);

    public override void _Ready()
    {
        Instance = this;
        // Keep GameManager running while the scene tree is paused so it can resume.
        ProcessMode = ProcessModeEnum.Always;
        GD.Print("Verdant Crown started.");
        SetState(GameState.Menu);
    }

    public void SetState(GameState next)
    {
        if (next == State) return;
        var old = State;
        State = next;
        EmitSignal(SignalName.StateChanged, (int)old, (int)next);
        GD.Print($"State: {old} → {next}");
    }

    /// <summary>Freezes the scene tree and enters Paused. Ignored unless currently Playing.</summary>
    public void Pause()
    {
        if (State != GameState.Playing) return;
        GetTree().Paused = true;
        SetState(GameState.Paused);
        EventBus.RaiseGamePaused(true);
    }

    /// <summary>Unfreezes the scene tree and returns to Playing. Ignored unless currently Paused.</summary>
    public void Resume()
    {
        if (State != GameState.Paused) return;
        GetTree().Paused = false;
        SetState(GameState.Playing);
        EventBus.RaiseGamePaused(false);
    }

    /// <summary>Toggles between Playing and Paused. Ignored in Boot/Menu.</summary>
    public void TogglePause()
    {
        if (State == GameState.Paused)
        {
            Resume();
        }
        else if (State == GameState.Playing)
        {
            Pause();
        }
    }
}
