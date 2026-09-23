using Godot;
using VerdantCrown.Core;
using VerdantCrown.World;

namespace VerdantCrown.UI;

/// <summary>
/// In-game HUD (CanvasLayer): player HP as a row of pip-hearts in the top-left, sized for the
/// 640x480 screen, plus a reserved pickup/status banner slot near the top.
/// Refreshes only when <see cref="EventBus.PlayerHudChanged"/> fires — it never polls game state.
/// </summary>
public partial class Hud : CanvasLayer
{
    /// <summary>Path to the hearts row (RichTextLabel), relative to this scene's root node.</summary>
    private const string HeartsPath = "Margin/Hearts";

    /// <summary>Path to the reserved status banner (Label), relative to this scene's root node.</summary>
    private const string BannerPath = "Banner";

    /// <summary>Path to PlayerHealth under the player node (group <see cref="RoomLoader.PlayerGroup"/>).</summary>
    private const string HealthNodePath = "Health";

    // Fallbacks rendered only when PlayerHealth can't be resolved, so the layout stays provable.
    private const int PlaceholderCurrentHp = 3;
    private const int PlaceholderMaxHp = 3;

    // Heart presentation (spec: at most six pip-hearts, legible on a 640x480 handheld).
    private const int MaxHearts = 6;
    private const char HeartGlyph = '\u2764'; // U+2764 BLACK HEART SUIT — unqualified (no VS16): plain-text heart.
    private const string FilledHeartColor = "#ff3333"; // bright red pip = current HP
    private const string EmptyHeartColor = "#4d4d4d"; // dark grey pip = lost HP

    private RichTextLabel? _hearts;
    private Label? _banner;
    private PlayerHealth? _playerHealth;
    private float _bannerSecondsLeft;

    public override void _Ready()
    {
        _hearts = GetNodeOrNull<RichTextLabel>(HeartsPath);
        if (_hearts is null)
        {
            GD.PushError($"Hud: hearts node '{HeartsPath}' not found — HP will not be displayed.");
        }

        _banner = GetNodeOrNull<Label>(BannerPath);
        if (_banner is null)
        {
            GD.PushError($"Hud: banner node '{BannerPath}' not found — status messages are unavailable.");
        }
        else
        {
            _banner.Visible = false; // The slot ships hidden; ShowBanner() reveals it.
        }

        ResolvePlayerHealth();

        EventBus.PlayerHudChanged += OnPlayerHudChanged;
        RefreshHearts();
    }

    public override void _ExitTree()
    {
        EventBus.PlayerHudChanged -= OnPlayerHudChanged;
    }

    /// <summary>Ticks the banner auto-hide countdown — the HUD's only per-frame work.</summary>
    public override void _Process(double delta)
    {
        if (_banner is not { Visible: true })
        {
            return;
        }

        _bannerSecondsLeft -= (float)delta;
        if (_bannerSecondsLeft <= 0f)
        {
            _banner.Visible = false;
        }
    }

    /// <summary>
    /// Shows the reserved pickup/status banner (e.g. "Locked") centered at the top of the screen
    /// for <paramref name="seconds"/>, then hides it automatically. Repeated calls replace the
    /// current message and restart the countdown; a non-positive duration (or empty message)
    /// hides it immediately. Nothing calls this yet — the later wiring pass is one line.
    /// </summary>
    public void ShowBanner(string message, float seconds)
    {
        if (_banner is null)
        {
            GD.PushWarning("Hud: ShowBanner called but the banner node is missing — message dropped.");
            return;
        }

        if (seconds <= 0f || string.IsNullOrEmpty(message))
        {
            _banner.Visible = false;
            return;
        }

        _banner.Text = message;
        _banner.Visible = true;
        _bannerSecondsLeft = seconds;
    }

    private void OnPlayerHudChanged()
    {
        RefreshHearts();
    }

    /// <summary>Binds the real PlayerHealth component on the player (group lookup, then Health child).</summary>
    private void ResolvePlayerHealth()
    {
        Node? player = GetTree().GetFirstNodeInGroup(RoomLoader.PlayerGroup);
        _playerHealth = player?.GetNodeOrNull<PlayerHealth>(HealthNodePath);
        if (_playerHealth is null)
        {
            GD.PushError("Hud: PlayerHealth not found — HP falls back to placeholder values.");
        }
    }

    /// <summary>Rewrites the heart row from live <see cref="PlayerHealth.Hp"/>/<see cref="PlayerHealth.HpMax"/>
    /// values (or the layout placeholder when unbound). Fires on every EventBus HUD refresh.</summary>
    internal void RefreshHearts()
    {
        if (_hearts is null)
        {
            return;
        }

        int current = _playerHealth is not null ? _playerHealth.Hp : PlaceholderCurrentHp;
        int max = _playerHealth is not null ? _playerHealth.HpMax : PlaceholderMaxHp;
        _hearts.Text = FormatHearts(current, max);
    }

    /// <summary>Heart-row BBCode: bright-red glyphs for current HP, dark-grey for the rest,
    /// capped at <see cref="MaxHearts"/> glyphs total.</summary>
    private static string FormatHearts(int current, int max)
    {
        int displayMax = Mathf.Clamp(max, 0, MaxHearts);
        int filledCount = Mathf.Clamp(current, 0, displayMax);
        int emptyCount = displayMax - filledCount;

        string filled = new(HeartGlyph, filledCount);
        if (emptyCount == 0)
        {
            return $"[color={FilledHeartColor}]{filled}[/color]";
        }

        string empty = new(HeartGlyph, emptyCount);
        return $"[color={FilledHeartColor}]{filled}[/color][color={EmptyHeartColor}]{empty}[/color]";
    }
}
