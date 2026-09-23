# Instrumentation — Verdant Crown's frame-time telemetry contract

**What this game logs, field by field, what the budget rules are, and how to read a line without
lying to yourself.** This file specifies how the game's R36S frame-time measurements are
recorded and interpreted; it is not a claim that every session or frame holds 60 fps. The discipline is the
the on-device methodology below: lock distributions, not desktop frame rates. This project
has no scenario harness or overlay; the game prints telemetry to stdout for later analysis.

The three sentences that govern everything below:

1. **A frame-time distribution, never a mean.** p50 / p95 / p99 / max / jit / over16 / over33.
2. **A number without its configuration is not a measurement** — and the configuration for these
   lines is: on-device (PortMaster path) vs headless/desktop, warm-up vs measure phase, per zone.
3. **The acceptance is per zone and never an average across zones.** `zone_summary` is the unit
   of verdict; `sample` lines are diagnostics.

---

## 1. Where the data lives

| where | written by | contains |
|---|---|---|
| terminal stdout | desktop runs | the whole stream (same lines as the device) |
| `/roms/ports/verdant-crown/log.txt` | the port's `.sh`, via `tee` | the whole stream, on device |
| `logs/` (repo) | a person, by copy | archived pulls, named by run |

The transport is **stdout**, not a side-channel file: every `GD.Print` the game makes already
tees to `log.txt` on the device, so telemetry rides the transport that exists.

- **Archive by rename; never truncate without saving the prior run.** The launcher preserves the
  previous `log.txt` as a timestamped `log-*.txt` before starting the new capture.
- **`grep '^sample,'` yields the telemetry stream and nothing else.** Every line kind starts with
  its own prefix at column 0 (`sample,` `zone_summary,` `warmup_end,` `assert,` `done,`);
  game prose (`Room: …`, `Room cleared: …`) never starts with those tokens. Anchor your greps.

---

## 2. The line format

Five kinds, **stable field order**, one line per event. Field order is a contract: append-only,
never reorder, never insert mid-line.

| kind | when | carries |
|---|---|---|
| `sample` | every ~1 s (§3) | one window's frame-time distribution + scene read-back |
| `warmup_end` | once, at 3.00 s | the warm-up boundary and how many frames it discarded |
| `zone_summary` | on leaving a room, and for the active room at quit | the same 13 fields, aggregated over the zone's whole measure-phase visit |
| `assert` | 2× per room entry, 3× per room exit | `assert,<rule>,<pass\|FAIL>` — the budget rules as code (§5) |
| `done` | once, on orderly quit | `done,<secs>,<samples>,<assert_fail>` |

### The shared 13-field layout

`sample` and `zone_summary` share one layout — the same shape, so the same cut works on both:

```
sample,<zone>,<p50_ms>,<p95_ms>,<p99_ms>,<max_ms>,<jit>,<over16>,<over33>,<fps_inst>,<nodes>,<tris_rendered>,<tris_scene>
```

Real lines, verbatim from the desktop headless smoke (their interpretation is a worked example
of trap §8.2 — these numbers are **work, not frames**):

```
sample,overworld_01,6.94,7.14,143.65,143.65,1.9543,2,2,144.00,24,0,1454      ← warm-up phase (before warmup_end): boot hitch in window
warmup_end,3.01,408                                                            ← 408 frames discarded
sample,overworld_01,6.90,6.93,6.94,6.94,0.0018,0,0,144.93,24,0,1454           ← measure phase, clean window
zone_summary,overworld_01,6.90,6.92,6.94,6.94,0.0016,0,0,144.93,24,0,1454     ← whole-visit aggregate → the verdict row
assert,mesh_nodes,pass
assert,tri_scene,pass
assert,p50_lock,FAIL
assert,jit,pass
assert,over33,pass
done,4.33,9,1
```

| # | field | meaning | source |
|---|---|---|---|
| 0 | kind | `sample` / `zone_summary` | emitter |
| 1 | `zone` | `rooms.json` current room id (`overworld_01`, `dungeon_01`…`dungeon_05`), `-` before the first room | `RoomLoader.CurrentRoomId` (already public — no Core change was needed) |
| 2 | `p50_ms` | median frame time, **nearest-rank** over the window (not interpolated: ~60 frames don't have the resolution an interpolation implies) | frame ring / zone window |
| 3 | `p95_ms` | 95th percentile, same rule | ″ |
| 4 | `p99_ms` | 99th percentile — at a 60-frame window this is the ~2nd-worst frame; near-max by construction | ″ |
| 5 | `max_ms` | worst frame **currently in the window** (taken from the sorted snapshot, so an evicted spike cannot report itself — smoke-caught bug, fixed pre-ship) | ″ |
| 6 | `jit` | **σ ÷ μ** (coefficient of variation) of the window's raw frame times. 0 = perfectly even. Acceptance: `< 0.05` | ″ |
| 7 | `over16` | frames strictly `> 16.67 ms` in the window. **Informational** — a healthy 60 lock can show a non-zero count (59.94 Hz presents land at 16.68) | ″ |
| 8 | `over33` | frames strictly `> 33.33 ms` in the window. **The playable count.** Acceptance: `= 0` per zone | ″ |
| 9 | `fps_inst` | **`1000 / p50_ms`, exactly** (displayed from the unrounded p50). Instantaneous rate implied by the median frame — never `Engine.GetFramesPerSecond()` and never the `TIME_FPS` monitor: both are ~1 s rolling averages that describe the *previous* state | derived |
| 10 | `nodes` | **resident mesh nodes** in the whole `Main` subtree (`MeshInstance3D` + `MultiMeshInstance3D` with a mesh), **walked once per room load and cached** — constant within a zone | `MeshSceneScan` walk (§4) |
| 11 | `tris_rendered` | mean of the renderer's own per-frame primitive counter over **exactly the window's frames** | `Performance.Monitor.RenderTotalPrimitivesInFrame`, sampled every frame |
| 12 | `tris_scene` | **resident scene triangles**, same walk/cache as `nodes`. Constant within a zone | `MeshSceneScan` walk (§4) |

`warmup_end,<secs>,<frames_discarded>` — the phase boundary. `done,<secs>,<samples>,<assert_fail>`
— `samples` counts every `sample` line (warm-up + measure); `assert_fail` counts every `FAIL`.

### `assert,<rule>,<pass|FAIL>` — attribution by adjacency

The assert line has no zone field (its format is fixed at three fields), so its zone is read by
position:

- **scene rules** (`mesh_nodes`, `tri_scene`) are emitted **on room entry**, after the previous
  zone's summary and **before the new zone's first sample** — their zone is *the zone of the next
  `sample` lines* (the values they check are the `nodes`/`tris_scene` fields of those lines);
- **frame rules** (`p50_lock`, `jit`, `over33`) are emitted **immediately after** the
  `zone_summary` they judge — same zone, same window, same numbers.

---

## 3. Windows, cadence, warm-up

| mechanism | value | why |
|---|---|---|
| sample ring | **240 frames** (4 s at 60 fps; 8 s at 30) | percentiles over a stable window; `sample` fields are computed over this ring |
| zone window | every measure-phase frame of the visit, until room change | `zone_summary` is the whole-visit distribution; grows ~0.5 MB/hour at 60 fps — bounded by session length |
| cadence | **emit when ≥ 1.0 s since last sample OR ≥ 60 frames** | the frame floor guarantees the stream on uncapped/starved loops (the headless smoke's clock only reaches 0.8 s in 90 frames — without the floor, no sample would exist); on the vsync-paced device both rules coincide at 1 s / 60 frames |
| **warm-up** | **first 3.00 s are discarded** (`WarmupSeconds`, named const in `FrameTelemetry.cs`) | the first heavy stage after idle reads **27–36 % slow** (shader compile / C# JIT / governor ramp) — that is not drift and must not enter a statistic. At the boundary the ring is cleared and `warmup_end,<secs>,<total frames recorded>` is emitted |

**Phase rule:** `sample` lines appearing **before** the run's `warmup_end` line are *warm-up
phase*. They are logged (the stream must be continuous and the boot hitch is worth seeing) but
**excluded from every conclusion**. `zone_summary` and all asserts are post-warm-up by
construction — the zone window only fills after the boundary. A run that never reaches
`warmup_end` (a sub-3 s smoke) can show samples but **cannot show a verdict**; `frame_stats.py`
says so explicitly.

**Pause:** `GameManager` pauses the scene tree; this node's `_Process` is gated with it, so
paused time is **not recorded** — a pause is a hole in the stream, not a slow frame.

**Windows and counters are one:** `jit`, percentiles, over-counts **and** `tris_rendered` are all
computed over the same frames (the renderer counter is accumulated per frame into the same
ring). Comparing a 1 s fps average
against a single-frame counter; that class of bug is structurally excluded here.

---

## 4. The scene read-back: `nodes`, `tris_scene`, `tris_rendered`

Two different sources, deliberately:

**Scene side (`nodes`, `tris_scene`) — one walk per room load, cached.**
`MeshSceneScan.Scan(Main)` runs when `RoomLoader.CurrentRoomId` changes (verified synchronous:
rooms, enemy visuals and pickups are all instantiated in `LoadRoom`'s own call, so the next
frame's poll sees the settled tree). Walking per frame would be the expensive live count this
contract avoids; the values are constant within a zone, so per-load sampling loses nothing.
The walk enforces two rules:

- triangle counts come from `Mesh.GetFaces().Length / 3`, **cached per mesh instance id** (the
  call copies every vertex — uncached it is the cost the per-load choice exists to avoid);
- a `MultiMesh` counts `perMeshTris × LIVE instances` (`VisibleInstanceCount`, −1 = all),
  never capacity — counting capacity once inflated the reference ratio by 1.26×. Depth-guarded
  at 24 so a rig can never turn a telemetry read into a stack overflow.

`nodes` is **whole-`Main`-subtree resident** (room + player rig + enemies), not "the room's
children": the per-node cliff is about what the renderer is asked to draw, and enemies live
under the room while the player is a Main sibling — scoping to the room would exclude exactly
the persistent nodes that still cost the frame. A node whose `Mesh` was swapped to `null`
(e.g. `EnemyBrain`'s grey-box slot after a visual attaches) is correctly not counted.

**Renderer side (`tris_rendered`) — `Performance.Monitor.RenderTotalPrimitivesInFrame`.**
Godot's `Performance.Monitor` enum includes `TIME_FPS`, `OBJECT_NODE_COUNT`,
`RENDER_TOTAL_OBJECTS_IN_FRAME`, `RENDER_TOTAL_PRIMITIVES_IN_FRAME`,
`RENDER_TOTAL_DRAW_CALLS_IN_FRAME` all exist. Choices:

- `RENDER_TOTAL_OBJECTS_IN_FRAME` was **rejected** for the `nodes` field: it counts *rendered
  objects of any type including 2D*, it cannot enforce the ≤64 *mesh-node* rule (§5), and its
  semantics differ from this document's `nodes` (= resident mesh nodes). The scene walk
  is the same quantity with an address.
- `RENDER_TOTAL_PRIMITIVES_IN_FRAME` **is** used for `tris_rendered` — read back off the
  renderer, per frame, averaged over the window.
- `TIME_FPS` / `Engine.GetFramesPerSecond()` are **never** used (rolling average; `fps_inst` is
  derived from our own frame times instead).

**Known gaps in this read-back, stated rather than discovered later:**

1. **`tris_rendered` includes 2D** (the HUD is CanvasItems, counted in the same total) — so it
   is informational here; there is no `t/s` assertion in this game's rule set, and `= 0` under
   `--headless` (no RenderingServer work) is expected, not a fault.
2. **No `draws` field.** The cost model's cliff is per-*node*, and this game holds one material
   per mesh (`MeshKit.ApplyVertexColors`), so draws ≈ mesh nodes; if a future change needs the
   number itself, `RENDER_TOTAL_DRAW_CALLS_IN_FRAME` is the verified monitor to add
   (append a field — never reorder).
3. A mesh that spawns **later than the load frame** (none exist today) would be invisible to the
   cached counts until the next room change.

---

## 5. Budget rules — as code, not discipline

Emitted by `FrameTelemetry` as `assert,<rule>,<pass|FAIL>`; limits are named constants in that
file. If a limit lives only in a document, a room eventually ships over it and nobody notices
until the device says so.

| rule | window | limit | emitted | rationale |
|---|---|---|---|---|
| `mesh_nodes` | scene walk | **≤ 64** | room entry | conservative resident-node limit for the tested handheld; adding mesh nodes can cost more than adding triangles |
| `tri_scene` | scene walk | **≤ 80 000** | room entry | provisional ceiling, not a measured Verdant Crown failure point: current rooms use far fewer triangles. Recalibrate with repeated device runs; record any threshold change here rather than quietly raising it to make a build green. |
| `p50_lock` | zone window | **p50 ∈ 16.67 ± 0.2 ms OR 33.33 ± 0.2 ms** | room exit | the frame is quantised: p50 sits ON a divisor or the game is not locked to anything (§6) |
| `jit` | zone window | **σ/μ < 0.05** | room exit | a lock is even; a plateau with fat dispersion is a struggle, not a lock |
| `over33` | zone window | **= 0** | room exit | the count that decides playability — and, at a p50 of 33.33, the signal that the 60 lock is *not* held (§6) |

`assert_fail` (field 4 of `done`) = number of `FAIL` lines in the run; `frame_stats.py`
cross-checks the two against each other.

---

## 6. The 60-lock acceptance — per zone, never averaged

**60-lock check, evaluated on every `zone_summary` line:**

```
p50_ms ∈ {16.67 ± 0.2} ∪ {33.33 ± 0.2}      jit < 0.05       over33 = 0
```

How the three interact, and what each combination means:

| p50 | jit | over33 | reading |
|---|---|---|---|
| ≈ 16.67 | < 0.05 | 0 | **60 LOCK — acceptance met** |
| ≈ 33.33 | < 0.05 | > 0 | **clean 30 lock**: content crossed the 16.67 line and the presenter dropped to the next divisor. Not a struggling frame — a *locked half rate*. The `p50_lock` rule passes (it is a lock), `over33` FAILs (the *60* lock is not held). Fix: less work in that zone — there is no graceful degradation (§8.1) |
| not near either | — | — | unpaced run or a genuinely erratic frame — **not a lock at all**. Check §8.2 before believing any of it |
| any | ≥ 0.05 | any | dispersion too high for a "lock" claim even if the median sits right — single spikes (loads, GC, a physics pile-up) are moving the frame |

**Never average across zones.** Averaging `overworld_01`'s 16.67 with `dungeon_03`'s 33.33
yields a number that describes no room in the game. The verdict is *each* `zone_summary`;
`sample` lines overlap by design (ring window) and are diagnostics, not votes.

**Measurement discipline that must surround any number cited from this stream:**
interleave repeats (±8 % is the cross-run floor), always discard to `warmup_end`, state the
configuration (device vs headless, phase, zone), and remember that fps is `1000/p50` of *our*
frames — never the engine's rolling average.

---

## 7. Reading it

**On the device** (the whole point — stdout tee):

```sh
grep 'sample,'    /roms/ports/verdant-crown/log.txt   # the stream
grep 'zone_summary,' /roms/ports/verdant-crown/log.txt # the verdicts
grep ',FAIL'      /roms/ports/verdant-crown/log.txt    # broken rules only
tail -1           <same file>                          # done, if the run exited cleanly
```

**On the desktop**, after pulling the log — `tools/frame_stats.py` (this game's telemetry
summarizer):

```sh
tools/frame_stats.py log.txt
```

```
samples: 9 (measure-phase: 3, warm-up-phase: 6); warmup_end at line 32
== zone summaries — 60-lock acceptance (per zone, NEVER averaged across zones) ==
  overworld_01     p50= 6.90 jit=0.0016 over33=0    nodes=24 tris_scene=1454 -> p50_lock=FAIL jit=pass over33=pass | 60-lock: NO
== asserts: 5 emitted, 1 FAIL ==
== done (line 42): secs=4.33 samples=9 assert_fail=1 ==
```

It ignores the launcher's headless pre-flight when `=== weston stage:` is present, then splits
warm-up from measure phase and aggregates measure-phase `sample` lines per zone
(diagnostics), **re-derives the acceptance from every `zone_summary` independently of the
game's own assert lines**, and cross-checks `done`'s `assert_fail` against the `FAIL` count.
The `p50_lock=FAIL` above is the headless trap §8.2 working as designed, *not* a device result.

---

## 8. Measurement traps

1. **Vsync quantisation.** A paced frame is 16.67 **or** 33.33, nothing between — one
   microsecond over budget and the presenter hands you exactly 2× the frame. So `p50` sitting
   dead on a divisor is a **lock, not a cost**; check `jit` to tell pacing from struggling.
   Corollary: `over16` can be non-zero on a perfectly healthy 60 lock (59.94 Hz presents land at
   16.68 ms, which is `> 16.67`) — **the lock verdict is `p50 ± 0.2` and `over33 = 0`, never
   `over16`.**
2. **Unpaced runs report work, not frames.** Headless (`--headless`) and any window with vsync
   off do not wait for the presenter: the run's own frame times are the *cost*, and `fps_inst`
   can read 144 — as it does in every example line above. Those smoke numbers prove the
   **pipeline**; they are **never** citable as device frame rates, and their
   `assert,p50_lock,FAIL` is expected (an unpaced p50 of 6.90 is correctly "not a lock").
3. **Teardown `Room cleared` artifacts.** On quit, enemy nodes leave the tree, `TreeExiting`
   fires, `RoomLoader`'s death-tracking hits zero and prints `Room cleared: <room>` /
   `Unlocked: …` **even though the player cleared nothing** (reproduced on every headless
   smoke). Lines after the last `sample` — especially around `done` — are teardown noise; `done`
   marks the real end of the run. Do not count teardown clears as progression evidence.
4. **`done` exists only on an orderly exit** (window close, `--quit-after`). A SIGKILL
   (`kill -9` or a forced timeout) loses it — an absent `done` means the run was
   killed, not that telemetry failed.
5. **Renderer counters are 0 under `--headless`** (no rendering happens) — `tris_rendered=0`
   in a smoke is expected; scene-side `tris_scene` still counts (it walks meshes, not frames).
6. **Warm-up-phase samples contain the boot hitch** and will show `jit ≈ 2`, `p99 ≈ 140 ms`.
   Locate `warmup_end` first, always; a log with no `warmup_end` has no measure phase and
   therefore no verdict.
7. **`jit` here is σ/μ**, not `max ÷ mean`. The acceptance `< 0.05` applies only to σ/μ;
   do not compare it with a different project's jitter statistic. `max_ms ÷ p50_ms`
   approximates the other quantity if needed.
8. **The window keeps a spike until it evicts.** After a room load, `max_ms`/`p99` stay elevated
   for up to 240 frames (4 s) even though the spike is gone — that is the ring working, not a
   regression. (Inverse bug — a max that *never* left — was caught by the first smoke and fixed:
   `max_ms` is now read from the same sorted snapshot as the percentiles.)

---

## 9. On / off

| control | effect |
|---|---|
| `[Export] TelemetryEnabled` on the `FrameTelemetry` node in `Main.tscn` | default **ON** (cost per frame: one window add + one counter; per second: one line) |
| `VC_NOTELEM=1` in the environment | **disables and silences** the node entirely (verified: zero telemetry lines, game unaffected). A device launch script can set it later |
| no F-key toggle | deliberate — a measurement affordance on a player-facing key is a defect waiting for a playtest |

The node is **independent**: no `[Export]` was added to `GameAutopilot`, no changes to
`Scripts/Core`, `Kit/`, combat/world/UI, levels, assets or data. The only game-layer
dependency is reading the already-public `RoomLoader.CurrentRoomId`.

---

## 10. Files and the extension contract

| file | role |
|---|---|
| `Scripts/Debug/FrameTelemetry.cs` | the node: schedule, warm-up, zone polling, emission, asserts. Hooked as `Main/FrameTelemetry` in `Scenes/Main/Main.tscn` |
| `Scripts/Debug/TelemetryWindow.cs` | distribution math (ring + growable modes): nearest-rank percentiles, σ/μ jitter, over-counts, window-mean of the counter |
| `Scripts/Debug/MeshSceneScan.cs` | the per-room-load scene walk (cached, depth-guarded, live-instance rule) |
| `tools/frame_stats.py` | desktop summariser + independent acceptance re-derivation |

**It lives in `Scripts/Debug/`, not `Kit/`**, because it is not game-agnostic: it reads
`RoomLoader.CurrentRoomId` and enforces *this* game's budget rules. `Kit/` stays the
game-agnostic layer (`MeshKit`, `StateMachine`, `InputEdges`) and was not touched.

**Extending the stream:** append fields to the end of a line, or add a new prefix-distinct kind,
and update this file in the same change. The thresholds in `tools/frame_stats.py` mirror
`FrameTelemetry.cs`'s constants — move them together or the two readers will disagree, and a
contract that reads differently in code than in the document is a contract nobody follows.
