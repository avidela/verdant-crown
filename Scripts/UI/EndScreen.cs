using Godot;
using Kit;
using VerdantCrown.Core;
using VerdantCrown.World;

namespace VerdantCrown.UI;

/// <summary>
/// Shared end-of-run overlay (CanvasLayer) for BOTH outcomes, following the
/// <see cref="PauseMenu"/> pattern: <c>ProcessMode = Always</c>, one centered panel with
/// big legible text for the 640x480 viewport plus a hint label, hidden by default.
/// Visibility is driven purely by <see cref="GameManager.StateChanged"/> (subscribed in
/// <c>_Ready</c>, unsubscribed in <c>_ExitTree</c>): GameOver shows "GAME OVER" /
/// "press J to retry", Victory shows "CROWN RECLAIMED!" / "press J to continue",
/// every other state hides the overlay.
/// <para>
/// This node also owns the Sprint 2.8 recovery input that closes the GameOver/Victory
/// softlock (ticket #2): while the state is GameOver or Victory, a rising edge on the
/// <c>attack</c> action (J) runs the recovery flow. GameOver revives the player
/// (<see cref="PlayerHealth.RestoreFull"/>) and reloads the CURRENT room fresh via
/// <see cref="RoomLoader.RetryCurrentRoom"/> (progression flags kept), then
/// <see cref="GameManager.GameState.Playing"/>. Victory restores the player and returns
/// to the hub overworld via <see cref="RoomLoader.ReturnToHub"/>, then resumes Playing
/// (post-victory roam; new-game+ out of scope). The edge is polled with
/// <see cref="InputEdge"/> — held-state tracking, never input events — so injected and
/// human presses behave identically. The state is checked BEFORE the edge acts, so each
/// press triggers at most one recovery and J means nothing in any other state.
/// </para>
/// </summary>
public partial class EndScreen : CanvasLayer
{
    /// <summary>Input action whose rising edge triggers recovery (bound to J in project.godot).</summary>
    private const string RetryAction = "attack";

    private const string TitleLabelPath = "Center/Panel/Layout/TitleLabel";
    private const string HintLabelPath = "Center/Panel/Layout/HintLabel";

    /// <summary>Player scene layout mirror: Main's "Player" child holds a "Health" node (see RoomLoader.PlayerGroup).</summary>
    private const string PlayerSiblingName = "Player";
    private const string PlayerHealthNodeName = "Health";

    private const string GameOverTitle = "GAME OVER";
    private const string GameOverHint = "press J to retry";
    private const string VictoryTitle = "CROWN RECLAIMED!";
    private const string VictoryHint = "press J to continue";

    private Label? _titleLabel;
    private Label? _hintLabel;
    private bool _subscribedToState;

    /// <summary>Rising-edge tracker for the recovery action; updated every frame, acted on only in the end states.</summary>
    private readonly InputEdge _retryEdge = new(RetryAction);

    public override void _Ready()
    {
        // End states never pause the tree, but Always mirrors PauseMenu and keeps the
        // edge tracker sampling even if a future state pauses things.
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;

        _titleLabel = GetNodeOrNull<Label>(TitleLabelPath);
        _hintLabel = GetNodeOrNull<Label>(HintLabelPath);
        if (_titleLabel is null || _hintLabel is null)
        {
            GD.PushError(
                $"EndScreen: missing labels ('{TitleLabelPath}' / '{HintLabelPath}') — " +
                "outcome text will not update.");
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null)
        {
            GD.PushError("EndScreen: GameManager autoload not ready — overlay and recovery disabled.");
            return;
        }

        gameManager.StateChanged += OnStateChanged;
        _subscribedToState = true;
        ApplyState(gameManager.State); // sync in case a state was set before this node was ready
    }

    public override void _ExitTree()
    {
        if (_subscribedToState && GameManager.Instance is not null)
        {
            GameManager.Instance.StateChanged -= OnStateChanged;
            _subscribedToState = false;
        }
    }

    public override void _Process(double delta)
    {
        _retryEdge.Update();
        if (!_retryEdge.Rising)
        {
            return;
        }

        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null)
        {
            return; // autoload torn down — nothing to recover from
        }

        // State check FIRST: one recovery per press, and J is inert outside the end states.
        if (gameManager.State == GameManager.GameState.GameOver)
        {
            RetryAfterGameOver(gameManager);
        }
        else if (gameManager.State == GameManager.GameState.Victory)
        {
            ContinueAfterVictory(gameManager);
        }
    }

    private void OnStateChanged(int oldState, int newState)
    {
        ApplyState((GameManager.GameState)newState);
    }

    private void ApplyState(GameManager.GameState state)
    {
        switch (state)
        {
            case GameManager.GameState.GameOver:
                SetText(GameOverTitle, GameOverHint);
                Visible = true;
                break;
            case GameManager.GameState.Victory:
                SetText(VictoryTitle, VictoryHint);
                Visible = true;
                break;
            default:
                Visible = false;
                break;
        }
    }

    private void SetText(string title, string hint)
    {
        if (_titleLabel is not null)
        {
            _titleLabel.Text = title;
        }

        if (_hintLabel is not null)
        {
            _hintLabel.Text = hint;
        }
    }

    /// <summary>GameOver recovery: revive → fresh reload of the current room → Playing.</summary>
    private void RetryAfterGameOver(GameManager gameManager)
    {
        PlayerHealth? health = ResolvePlayerHealth();
        RoomLoader? loader = ResolveRoomLoader();
        if (health is null || loader is null)
        {
            // Stay in GameOver — the next press re-runs the lookup instead of resuming a
            // broken session (no new limbo).
            GD.PrintErr("EndScreen: recovery nodes missing — GameOver retry skipped.");
            return;
        }

        health.RestoreFull();
        if (!loader.RetryCurrentRoom())
        {
            // LoadRoom's contract: failure keeps the current room — resume there rather
            // than risk stranding the player on the end screen.
            GD.PrintErr("EndScreen: current-room reload failed — resuming in the room as-is.");
        }

        gameManager.SetState(GameManager.GameState.Playing);
        GD.Print("Retry: back to Playing with a fresh room.");
    }

    /// <summary>Victory recovery: restore → hub overworld → Playing (post-victory roam).</summary>
    private void ContinueAfterVictory(GameManager gameManager)
    {
        PlayerHealth? health = ResolvePlayerHealth();
        RoomLoader? loader = ResolveRoomLoader();
        if (health is null || loader is null)
        {
            GD.PrintErr("EndScreen: recovery nodes missing — Victory continue skipped.");
            return;
        }

        health.RestoreFull();
        if (!loader.ReturnToHub())
        {
            GD.PrintErr("EndScreen: hub return failed — resuming in the room as-is.");
        }

        gameManager.SetState(GameManager.GameState.Playing);
        GD.Print("Continue: back to Playing at the hub.");
    }

    private RoomLoader? ResolveRoomLoader()
    {
        RoomLoader? loader = GetParent()?.GetNodeOrNull<RoomLoader>("RoomLoader");
        if (loader is null)
        {
            GD.PrintErr("EndScreen: RoomLoader not found as a sibling — recovery unavailable.");
        }

        return loader;
    }

    private PlayerHealth? ResolvePlayerHealth()
    {
        Node? player = GetTree()?.GetFirstNodeInGroup(RoomLoader.PlayerGroup)
            ?? GetParent()?.GetNodeOrNull<Node>(PlayerSiblingName);
        PlayerHealth? health = player?.GetNodeOrNull<PlayerHealth>(PlayerHealthNodeName);
        if (health is null)
        {
            GD.PrintErr($"EndScreen: player '{PlayerHealthNodeName}' node not found — recovery unavailable.");
        }

        return health;
    }
}
