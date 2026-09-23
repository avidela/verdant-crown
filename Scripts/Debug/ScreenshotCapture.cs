using System;
using Godot;
using Kit;

namespace VerdantCrown.Debug;

/// <summary>
/// In-game screenshot capture: the rising edge of the <c>screenshot</c> action
/// (physical F3) grabs the viewport and writes a timestamped PNG to <c>user://</c>.
/// <para>The R36S port runs standalone. Press L1 (mapped to F3), and the launch
/// script prints the resulting PNG path in <c>log.txt</c>. The image can also be
/// copied off the card for diagnostics or sharing.</para>
/// <para><b>Save paths</b> (<c>user://screenshot_yyyyMMdd_HHmmss.png</c>, UTC):</para>
/// <list type="bullet">
/// <item>Desktop: <c>~/.local/share/godot/app_userdata/Verdant Crown/</c>
/// (i.e. <c>$XDG_DATA_HOME/godot/app_userdata/Verdant Crown/</c>).</item>
/// <item>Device: the port's launch script sets <c>XDG_DATA_HOME</c> to the port's conf
/// dir, so the file lands under <c>/roms/ports/verdant-crown/…/godot/app_userdata/
/// Verdant Crown/</c> — inside the port directory, trivially scp-able. The
/// <c>Screenshot saved: &lt;globalized path&gt;</c> print is what reveals the exact
/// location in <c>log.txt</c>.</item>
/// </list>
/// <para>Input uses the <see cref="InputEdge"/> rising-edge pattern (poll held state)
/// rather than <c>IsActionJustPressed</c>, matching GameAutopilot — remote injection
/// only sets held state, and the edge tracker works for both human and injected input.
/// No <c>[Export]</c> gating: F3 only, harmless in every game state.</para>
/// </summary>
public partial class ScreenshotCapture : Node
{
    /// <summary>Input action defined in project.godot [input] — physical F3 (keycode 4194334).</summary>
    private const string ScreenshotAction = "screenshot";

    private InputEdge? _edge;
    private int _autoRemaining; // VC_AUTOSHOOT countdown ticks (0 = off)

    /// <summary>Constructs the edge tracker only — the viewport/texture are resolved
    /// at capture time, nothing hot is cached in <c>_Ready</c>.</summary>
    public override void _Ready()
    {
        _edge = new InputEdge(ScreenshotAction);
        // Device/auto harness hook (overnight directive 2026-09-22): VC_AUTOSHOOT=1
        // captures one frame after the game settles (default 180 ticks ≈ 3 s);
        // VC_AUTOSHOOT=<n> overrides the tick count. No keypress exists remotely,
        // so the env var is how the AI gets a picture out of the handheld.
        string auto = OS.GetEnvironment("VC_AUTOSHOOT");
        if (auto.Length > 0)
        {
            // Small/flag values mean "use the default settle delay": the viewport
            // texture is null for the first frames (log-proven on device), so a raw
            // tick count under a sensible floor is never honored literally.
            _autoRemaining = int.TryParse(auto, out int ticks) && ticks >= 60 ? ticks : 180;
            GD.Print($"ScreenshotCapture: auto-shoot armed in {_autoRemaining} ticks (VC_AUTOSHOOT={auto})");
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_autoRemaining > 0)
        {
            _autoRemaining--;
            if (_autoRemaining == 0)
            {
                Capture();
            }
        }

        _edge!.Update();
        if (_edge.Rising)
        {
            Capture();
        }
    }

    /// <summary>Grabs the current viewport frame and saves it as a timestamped PNG,
    /// logging the globalized path on success (device log.txt) or a PrintErr on failure.</summary>
    private void Capture()
    {
        Viewport? viewport = GetViewport();
        if (viewport?.GetTexture() is null)
        {
            GD.PrintErr("Screenshot failed: viewport or its texture not ready yet");
            return;
        }
        Image? img = viewport.GetTexture()!.GetImage();
        if (img is null)
        {
            GD.PrintErr("Screenshot failed: viewport texture image was null");
            return;
        }

        string path = $"user://screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png";
        Error err = img.SavePng(path);
        if (err != Error.Ok)
        {
            GD.PrintErr($"Screenshot save failed: {err} path={ProjectSettings.GlobalizePath(path)}");
            return;
        }

        GD.Print($"Screenshot saved: {ProjectSettings.GlobalizePath(path)}");
    }
}
