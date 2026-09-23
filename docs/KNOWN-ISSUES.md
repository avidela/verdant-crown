# Verdant Crown — Known Issues

Open issues only; resolved issues remain in git history. Numbers are stable but gappy.
This is a project backlog, **not** a PortMaster tutorial: the standalone build and packaging
instructions are in the root [README](../README.md). The limitations below do not prevent the
recorded end-to-end playthrough.

## Gameplay

| # | Issue | Source | Notes |
|---|---|---|---|
| 1 | `temple_key` declared but unimplemented | QA 2026-09 finding #4 | rooms.json d03 declares `id=key, item_id=temple_key`; no pickup node in Dungeon03, `HasKey()`/`AddKeyItem()` never called, no door checks it. Winnability unaffected (d05 gated on d03 clear only). To finish: pickup node + pickup script + boss-door gate. |
| 3 | No title screen; PauseMenu "Quit to Menu" is a stub | QA finding #9 | GameManager boots straight to Playing via RoomLoader. Needs GameMenus (Kit) + menu state wiring. |
| 5 | Stale test scene `Whitebox.tscn` contains its own Player | QA finding #10 | Keep out of exports; delete when no longer useful as a scratch scene. |
| 6 | Legacy `EnemyDummy.cs` only used by Whitebox | Combat 2.6 | Remove with Whitebox or keep as a minimal IDamageable example. |
| 7 | Boss-room reinforcement slime not counted for room clear | Combat 2.6 (deliberate) | Room clears on declared enemies only; spawned slime can still hit you afterwards. Decide: count spawned, or despawn on clear. |

## Device / tooling

| # | Issue | Source | Notes |
|---|---|---|---|
| 4 | Heart pickup has no VFX or sound | playtest | Walk-over heal works; visual feedback can be improved. |
| 15 | Sky/void edges: camera sees past the 3 m walls to BLACK | device screenshot | Unshaded world, no sky/clear-color: over-the-wall views end in black void. Polish: pleasant window clear-color or cheap sky. Not a perf issue. |
| 16 | No audio | device log 2026-09-23 | No audio assets ship. The tested device's ALSA driver fails to open and falls back to dummy audio. Audio is outside this vertical slice; adding sound requires testing a working device output path. |
| 19 | Transition hitches and thin 60 fps margin | device telemetry 2026-09-22/23 | Door-load frames reached 41–148 ms despite a 16.67 ms median in all eight human-play zones. The tested first cold run and pilot-enabled runs held 33.33 ms; the warm human run held 16.67 ms. Re-measure on device after adding per-frame work; no claim of an unconditional 60 fps lock. See [instrumentation](INSTRUMENTATION.md). |
| 24 | Telemetry harvest gaps on non-human runs | run4 2026-09-23 | A stationary run emits no `zone_summary` (only `sample,` lines), `timeout` SIGKILL skips the graceful `done`, and stdout buffering can truncate tails. Fixes pending: periodic zone_summary for the current zone + signal-safe final flush; harvesters must also read `sample,` lines. |

## Polish / art backlog

- Sword swing still rides a TEMP DEBUG arm-resolution path — the log prints `Sword: TEMP DEBUG swing arm resolved` on every boot — and sits on a grey box: needs the real rig/wiring (see game-brief).
- Arches: doorway gaps cut, but wall/door materials all grey — art pass.
- No walk bob / death animation / hit-flash on player; enemy death is QueueFree (no poof).
- **Room name in HUD:** show `rooms.json` `display_name` for the current room (`RoomLoader.CurrentRoomId`).
- **Enemy counter in HUD:** expose `RoomLoader._aliveEnemies` to the HUD.
- **Brick/stone painted courses on temple walls** (deferred polish): subdivide wall masses + paint staggered mortar rows & moss via the models' paint() technique (zero textures, unshaded-safe); optional tiny tile texture only as a measured experiment. Declared art direction: verdant fields over a ruined stone temple (not cave, not castle).
- HUD: HP text only; hearts display, key-item icons, messages (Locked: …) not shown on screen.
