using System.Globalization;
using Godot;
using VerdantCrown.Core;

namespace VerdantCrown.Debug;

/// <summary>
/// Frame-time telemetry: per-interval distributions, per-zone summaries and
/// budget assertions as log lines. Contract: docs/INSTRUMENTATION.md.
/// <para>Line kinds on stdout (the port tees stdout to log.txt on device):
/// <c>sample,</c> ~1 s cadence · <c>warmup_end,</c> once · <c>zone_summary,</c> on room
/// change and at quit · <c>assert,&lt;rule&gt;,&lt;pass|FAIL&gt;</c> per budget rule per
/// zone · <c>done,</c> on orderly quit. Field order is stable; append-only.</para>
/// <para>Independent of GameAutopilot and of Scripts/Core changes: the zone comes from
/// <see cref="RoomLoader.CurrentRoomId"/> (already public), the frame times from this node's
/// own <c>_Process</c> deltas — never <c>Engine.GetFramesPerSecond()</c> (a ~1 s rolling
/// average that describes the PREVIOUS state). On by default; <c>VC_NOTELEM=1</c> disables.</para>
/// </summary>
public partial class FrameTelemetry : Node
{
    // --- Schedule / window -------------------------------------------------
    /// <summary>Nominal sample cadence (the ~1 s interval of the contract).</summary>
    private const double SampleIntervalSeconds = 1.0;

    /// <summary>
    /// Emission floor: a sample also fires every N frames even if the clock has not advanced
    /// a full interval. An uncapped or starved loop (headless smoke) must never suppress the
    /// stream; on the vsync-paced device both rules coincide (60 frames = 1 s).
    /// </summary>
    private const int SampleMinFrames = 60;

    /// <summary>Ring = 4 s of frames at 60 fps; percentiles are computed over this window.</summary>
    private const int RingCapacity = 240;

    /// <summary>
    /// Warm-up discard: the first heavy stage after idle reads 27–36 % slow (shader compile /
    /// JIT / governor ramp — not drift). Frames before this line are excluded from every
    /// statistic a conclusion may rest on; the boundary is the <c>warmup_end</c> line.
    /// </summary>
    private const double WarmupSeconds = 3.0;

    // --- Budget rules (the acceptance, as code) ----------------------------
    /// <summary>The frame is quantised: p50 sits ON 16.67 (60 lock) or 33.33 (30 lock), ± this.</summary>
    private const double LockFastMs = 1000.0 / 60.0; // 16.666…
    private const double LockSlowMs = 1000.0 / 30.0; // 33.333…
    private const double LockToleranceMs = 0.2;

    private const double Over16ThresholdMs = 16.67;
    private const double Over33ThresholdMs = 33.33;

    /// <summary>60-lock acceptance: σ÷μ of the zone's frame times below this.</summary>
    private const double MaxJitter = 0.05;

    /// <summary>RULE 1 of the cost model: the per-node curve is flat to 64, then superlinear.</summary>
    private const int MaxMeshNodes = 64;

    /// <summary>
    /// Resident-triangle ceiling. Provisional at 80 000 — the reference project re-set its own
    /// limit to this after device measurement (a resident pair of 68,472 held the 60 lock);
    /// recalibrate with the first Verdant Crown device window and record WHY it moved here.
    /// </summary>
    private const long MaxSceneTriangles = 80_000;

    // --- Wiring ------------------------------------------------------------
    private const string RoomLoaderNodeName = "RoomLoader";
    private const string ZoneNone = "-";
    private const string DisableEnvVar = "VC_NOTELEM";
    private const string DisableEnvValue = "1";

    /// <summary>Master switch for this measurement build. Cleared by <c>VC_NOTELEM=1</c>.</summary>
    [Export] public bool TelemetryEnabled { get; set; } = true;

    private TelemetryWindow _ring = null!;
    private TelemetryWindow _zoneWindow = null!;
    private RoomLoader? _loader;

    private string _zone = ZoneNone;
    private int _zoneMeshNodes;   // scene-side read-back, cached per room load (see docs §4)
    private long _zoneSceneTris;

    private double _clock;
    private double _secondsSinceSample;
    private int _framesSinceSample;
    private int _framesRecorded; // every frame ever accepted; at warm-up end these are the discarded ones
    private long _sampleCount;
    private long _assertFailures;
    private bool _warmedUp;
    private bool _active;
    private bool _finished;

    /// <summary>Distribution snapshot of one window: computed once, reused by line and asserts.</summary>
    private readonly record struct Dist(
        double P50, double P95, double P99, double Max, double Jit,
        long Over16, long Over33, double MeanPrims);

    public override void _Ready()
    {
        _active = TelemetryEnabled && OS.GetEnvironment(DisableEnvVar) != DisableEnvValue;
        if (!_active)
        {
            SetProcess(false); // VC_NOTELEM=1 (or export off): this node is inert and silent
            return;
        }

        _ring = new TelemetryWindow(RingCapacity);
        _zoneWindow = new TelemetryWindow();
        // Main composes RoomLoader as a sibling child; null fallback, never assumed.
        _loader = GetParent()?.GetNodeOrNull<RoomLoader>(RoomLoaderNodeName);
    }

    public override void _Process(double delta)
    {
        if (!_active)
        {
            return;
        }

        double ms = delta * 1000.0;
        if (ms <= 0 || double.IsNaN(ms))
        {
            return; // clock glitch / paused frame — not a measurement
        }

        _clock += delta;
        _framesRecorded++;
        PollZone(); // attribute THIS frame to the zone it completes in

        // Renderer primitives sampled every frame so the counter's mean covers EXACTLY the
        // same frames as the percentiles (rate and counters must share one window).
        double prims = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        _ring.Add(ms, prims);
        if (_warmedUp)
        {
            _zoneWindow.Add(ms, prims);
        }

        if (!_warmedUp && _clock >= WarmupSeconds)
        {
            EndWarmup();
        }

        _secondsSinceSample += delta;
        _framesSinceSample++;
        if (_secondsSinceSample >= SampleIntervalSeconds || _framesSinceSample >= SampleMinFrames)
        {
            EmitSample();
        }
    }

    /// <summary>
    /// Room change: close the old zone (summary + frame-rule asserts), open the new one
    /// (fresh window + one scene walk + scene-rule asserts). Runs before the frame is
    /// recorded, so the frame belongs to the zone it finishes in.
    /// </summary>
    private void PollZone()
    {
        string id = _loader?.CurrentRoomId ?? string.Empty;
        string zone = id.Length > 0 ? id : ZoneNone;
        if (zone == _zone)
        {
            return;
        }

        if (_zone != ZoneNone && _zoneWindow.Count > 0)
        {
            Dist d = DistOf(_zoneWindow);
            GD.Print(LineOf("zone_summary", _zone, d, _zoneMeshNodes, _zoneSceneTris));
            EmitFrameAsserts(d);
        }

        _zone = zone;
        _zoneWindow.Reset();
        _zoneMeshNodes = 0;
        _zoneSceneTris = 0;
        if (zone == ZoneNone)
        {
            return;
        }

        // One walk per room load: resident counts are constant within a zone, so caching them
        // here (instead of per frame) is the documented cost choice of the contract.
        if (GetParent() is Node root)
        {
            ScanResult scan = MeshSceneScan.Scan(root);
            _zoneMeshNodes = scan.MeshNodes;
            _zoneSceneTris = scan.SceneTris;
        }

        EmitAssert("mesh_nodes", _zoneMeshNodes <= MaxMeshNodes);
        EmitAssert("tri_scene", _zoneSceneTris <= MaxSceneTriangles);
    }

    private void EndWarmup()
    {
        // discarded = EVERY frame recorded so far (not ring.Count — a fast loop records more
        // frames in 3 s than the 240-slot ring keeps, and the honest number is the true count).
        GD.Print($"warmup_end,{F2(_clock)},{_framesRecorded}");
        _warmedUp = true;
        _ring.Reset();      // discard warm-up frames from every statistic
        _zoneWindow.Reset();
        _secondsSinceSample = 0;
        _framesSinceSample = 0;
    }

    private void EmitSample()
    {
        _secondsSinceSample = 0;
        _framesSinceSample = 0;
        if (_ring.Count == 0)
        {
            return; // just past warm-up: let the fresh ring fill one interval first
        }

        _sampleCount++;
        GD.Print(LineOf("sample", _zone, DistOf(_ring), _zoneMeshNodes, _zoneSceneTris));
    }

    /// <summary>The one field layout of the contract. Kind and zone first, stats, then read-backs.</summary>
    private static string LineOf(string kind, string zone, in Dist d, int nodes, long sceneTris)
    {
        double fps = d.P50 > 0 ? 1000.0 / d.P50 : 0; // fps_inst = 1000 / p50, stated in docs
        return string.Join(',',
            kind, zone,
            F2(d.P50), F2(d.P95), F2(d.P99), F2(d.Max), F4(d.Jit),
            d.Over16.ToString(CultureInfo.InvariantCulture),
            d.Over33.ToString(CultureInfo.InvariantCulture),
            F2(fps),
            nodes.ToString(CultureInfo.InvariantCulture),
            ((long)Math.Round(d.MeanPrims)).ToString(CultureInfo.InvariantCulture),
            sceneTris.ToString(CultureInfo.InvariantCulture));
    }

    private static Dist DistOf(TelemetryWindow w)
    {
        (double p50, double p95, double p99, double max) = w.Percentiles();
        return new Dist(p50, p95, p99, max, w.Jitter,
            w.Over(Over16ThresholdMs), w.Over(Over33ThresholdMs), w.MeanPrims);
    }

    /// <summary>The three frame rules, evaluated per zone over that zone's measure-phase frames.</summary>
    private void EmitFrameAsserts(in Dist d)
    {
        bool onLock = Math.Abs(d.P50 - LockFastMs) <= LockToleranceMs
                   || Math.Abs(d.P50 - LockSlowMs) <= LockToleranceMs;
        EmitAssert("p50_lock", onLock);
        EmitAssert("jit", d.Jit < MaxJitter);
        EmitAssert("over33", d.Over33 == 0);
    }

    private void EmitAssert(string rule, bool pass)
    {
        if (!pass)
        {
            _assertFailures++;
        }

        GD.Print($"assert,{rule},{(pass ? "pass" : "FAIL")}");
    }

    /// <summary>
    /// Orderly quit only (window close, <c>--quit-after</c>): SceneTree teardown propagates
    /// NOTIFICATION_EXIT_TREE through every node, so this fires on every clean exit path.
    /// A SIGKILL loses the line — documented in the traps section.
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationExitTree && _active && !_finished)
        {
            Finish();
        }
    }

    private void Finish()
    {
        _finished = true;
        if (_zone != ZoneNone && _zoneWindow.Count > 0)
        {
            Dist d = DistOf(_zoneWindow);
            GD.Print(LineOf("zone_summary", _zone, d, _zoneMeshNodes, _zoneSceneTris));
            EmitFrameAsserts(d);
        }

        GD.Print($"done,{F2(_clock)},{_sampleCount},{_assertFailures}");
    }

    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string F4(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
}
