using Godot;
using VerdantCrown.Core;

namespace VerdantCrown.UI;

/// <summary>
/// Pause overlay (CanvasLayer): hidden by default, <c>ProcessMode = Always</c> so it keeps
/// ticking while the tree is paused. Shows a centered "PAUSED" panel with Resume and
/// Quit to Menu buttons, sized as large click targets for the 640x480 handheld.
/// <para>
/// The <c>pause</c> action is edge-detected manually (track held state, fire on the rising
/// edge) because <c>Input.IsActionJustPressed</c> is unreliable under injected/remote input.
/// </para>
/// <para>
/// <c>GetTree().Paused</c> is only ever changed by <see cref="GameManager"/>; this script
/// calls <see cref="GameManager.TogglePause"/> and reacts to <see cref="EventBus.GamePaused"/>.
/// </para>
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    /// <summary>Input action that toggles the pause menu (bound to Escape in project.godot).</summary>
    private const string PauseAction = "pause";

    private const string ResumeButtonPath = "Center/Panel/Layout/ResumeButton";
    private const string QuitButtonPath = "Center/Panel/Layout/QuitButton";

    private Button? _resumeButton;
    private Button? _quitButton;

    /// <summary>Previous-frame held state of the pause action, for manual rising-edge detection.</summary>
    private bool _pauseWasHeld;

    public override void _Ready()
    {
        // Must run while GetTree().Paused so the pause key and buttons work when paused.
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;

        _resumeButton = GetNodeOrNull<Button>(ResumeButtonPath);
        _quitButton = GetNodeOrNull<Button>(QuitButtonPath);

        if (_resumeButton is null || _quitButton is null)
        {
            GD.PushError(
                $"PauseMenu: missing buttons ('{ResumeButtonPath}' / '{QuitButtonPath}') — " +
                "menu will be visible but not interactive.");
        }
        else
        {
            _resumeButton.Pressed += OnResumePressed;
            _quitButton.Pressed += OnQuitPressed;
        }

        // Visibility follows GameManager's pause state, so any pauser (key, button,
        // future damage cutscene) shows/hides this menu consistently.
        EventBus.GamePaused += OnGamePaused;
    }

    public override void _ExitTree()
    {
        EventBus.GamePaused -= OnGamePaused;

        if (_resumeButton is not null)
        {
            _resumeButton.Pressed -= OnResumePressed;
        }

        if (_quitButton is not null)
        {
            _quitButton.Pressed -= OnQuitPressed;
        }
    }

    public override void _Process(double delta)
    {
        // Manual rising-edge detection: Input.IsActionJustPressed can be missed under
        // injected/remote input, so track the held state ourselves and fire on false → true.
        bool held = Input.IsActionPressed(PauseAction);
        if (held && !_pauseWasHeld)
        {
            TogglePause();
        }

        _pauseWasHeld = held;
    }

    private void TogglePause()
    {
        GameManager? gameManager = GameManager.Instance;
        if (gameManager is null)
        {
            // Autoload not ready yet (Boot); nothing to toggle.
            return;
        }

        // GameManager owns GetTree().Paused — this never touches the tree directly.
        gameManager.TogglePause();
    }

    private void OnGamePaused(bool paused)
    {
        Visible = paused;
        if (paused)
        {
            _resumeButton?.GrabFocus();
        }
    }

    private void OnResumePressed()
    {
        TogglePause();
    }

    private void OnQuitPressed()
    {
        // TODO(menu): route to the main menu once it exists; for now just log the intent.
        GD.Print("PauseMenu: Quit to Menu requested — main menu not implemented yet.");
    }
}
