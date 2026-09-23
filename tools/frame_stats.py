#!/usr/bin/env python3
"""Verdant Crown telemetry summarizer — the desktop reader for the on-device log.

Contract: docs/INSTRUMENTATION.md. Parses the line kinds emitted by
Scripts/Debug/FrameTelemetry.cs (sample / zone_summary / warmup_end / assert / done),
prints per-zone tables and independently re-checks the 60-lock acceptance rules from
the zone_summary numbers. Pure stdlib; reads a file or stdin:

    tools/frame_stats.py log.txt            # a pulled device log
    grep 'sample,' log.txt | ... | tools/frame_stats.py   # any line stream

NOTE: the threshold constants mirror FrameTelemetry.cs — if the code's budget
changes, change them here too (they are the same contract, in two places).
"""
import sys

# --- contract constants (mirrors FrameTelemetry.cs) -------------------------
LOCK_FAST, LOCK_SLOW, LOCK_TOL = 16.67, 33.33, 0.2
OVER16_T, OVER33_T = 16.67, 33.33
MAX_JIT = 0.05
SAMPLE_FIELDS = ["kind", "zone", "p50", "p95", "p99", "max", "jit",
                 "over16", "over33", "fps_inst", "nodes", "tris_rend", "tris_scene"]


def acceptance(p50, jit, over33):
    """Recompute the three frame rules from a zone_summary line (independent check)."""
    on_lock = abs(p50 - LOCK_FAST) <= LOCK_TOL or abs(p50 - LOCK_SLOW) <= LOCK_TOL
    return [("p50_lock", on_lock), ("jit", jit < MAX_JIT), ("over33", over33 == 0)]


def main(path=None):
    lines = sys.stdin if path in (None, "-") else open(path, encoding="utf-8", errors="replace")
    samples, summaries, asserts, dones, warmup_at = [], [], [], [], None
    for lineno, raw in enumerate(lines, 1):
        s = raw.strip()
        # The PortMaster launcher runs a headless pre-flight first; its telemetry
        # (including a `done` line) must not be mixed with the Weston gameplay run.
        if s.startswith("=== weston stage:"):
            samples, summaries, asserts, dones, warmup_at = [], [], [], [], None
            continue
        if s.startswith("sample,"):
            f = s.split(",")
            if len(f) == len(SAMPLE_FIELDS):
                samples.append((lineno, warmup_at is not None, f))
        elif s.startswith("zone_summary,"):
            f = s.split(",")
            if len(f) == len(SAMPLE_FIELDS):
                summaries.append((lineno, f))
        elif s.startswith("warmup_end,"):
            warmup_at = lineno
        elif s.startswith("assert,"):
            f = s.split(",")
            if len(f) == 3:
                asserts.append((lineno, f[1], f[2]))
        elif s.startswith("done,"):
            dones.append((lineno, s.split(",")))

    def num(x):
        return float(x)

    print(f"samples: {len(samples)} "
          f"(measure-phase: {sum(1 for _, m, _ in samples if m)}, "
          f"warm-up-phase: {sum(1 for _, m, _ in samples if not m)}); "
          f"warmup_end at line {warmup_at if warmup_at else 'NEVER (whole run is warm-up phase)'}")

    # Per-zone sample stats: warm-up-phase samples are excluded from every conclusion.
    zones = {}
    for _, phase, f in samples:
        if phase:  # phase=True means the line is AFTER warmup_end (measure phase)
            zones.setdefault(f[1], []).append(f)
    if zones:
        print("\n== measure-phase samples, per zone (diagnostics — the verdict is zone_summary) ==")
        print(f"{'zone':<16}{'n':>4}{'p50_min':>9}{'p50_max':>9}{'jit_max':>9}"
              f"{'over33':>8}{'fps_min':>9}{'nodes':>7}{'tris_rend_max':>15}{'tris_scene':>11}")
        for z, fs in sorted(zones.items()):
            print(f"{z:<16}{len(fs):>4}"
                  f"{min(num(f[2]) for f in fs):>9.2f}{max(num(f[2]) for f in fs):>9.2f}"
                  f"{max(num(f[6]) for f in fs):>9.4f}"
                  f"{sum(int(f[8]) for f in fs):>8}"
                  f"{min(num(f[9]) for f in fs):>9.2f}"
                  f"{max(int(f[10]) for f in fs):>7}"
                  f"{max(int(f[11]) for f in fs):>15}"
                  f"{max(int(f[12]) for f in fs):>11}")
    else:
        print("\n(no measure-phase samples)")

    # zone_summary = the acceptance unit (whole visit, post-warm-up only).
    print("\n== zone summaries — 60-lock acceptance (per zone, NEVER averaged across zones) ==")
    if not summaries:
        print("  none: no zone survived past warm-up with frames — no verdict can be drawn")
    for _, f in summaries:
        p50, jit, o33 = num(f[2]), num(f[6]), int(f[8])
        verdict = " ".join(f"{r}={'pass' if ok else 'FAIL'}" for r, ok in acceptance(p50, jit, o33))
        sixty = abs(p50 - LOCK_FAST) <= LOCK_TOL and o33 == 0
        print(f"  {f[1]:<16} p50={p50:>6.2f} jit={jit:.4f} over33={o33:<4} "
              f"nodes={f[10]} tris_scene={f[12]} -> {verdict} | 60-lock: {'YES' if sixty else 'NO'}")

    fails = [(ln, r) for ln, r, res in asserts if res == "FAIL"]
    print(f"\n== asserts: {len(asserts)} emitted, {len(fails)} FAIL ==")
    for ln, rule in fails:
        print(f"  line {ln}: assert,{rule},FAIL")

    if dones:
        ln, f = dones[-1]
        print(f"\n== done (line {ln}): secs={f[1]} samples={f[2]} assert_fail={f[3]} ==")
        if len(f) > 3 and fails and int(f[3]) != len(fails):
            print(f"  MISMATCH: done says {f[3]} fails, log contains {len(fails)}")
    else:
        print("\n== NO done LINE: run did not exit cleanly (SIGKILL / crash / still running) ==")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else None))
