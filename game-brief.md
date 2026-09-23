# Game Brief — Verdant Crown

- **Genre**: 3D low-poly action-adventure, top-down camera (Zelda-like)
- **Perspective**: 3D — Blender low-poly GLBs, unshaded vertex colors, fixed top-down/angled camera with right-stick nudge
- **Core Fantasy**: Explore a verdant overworld, delve ruins, fight with sword + dodge, beat a boss, reclaim the crown
- **Setting**: Overworld adventure — grass fields, ruins, dungeons; fantasy tone
- **Scope**: Vertical slice — one overworld hub + ~5 dungeon rooms, 1 boss, winnable end-to-end (~10–15 min)
- **Controls (R36S)**: Left stick/d-pad move, A attack, B dodge, Start pause; Select+Start quit. Right stick camera is not implemented.
- **Target device**: R36S (RK3326, 640×480, ArkOS, PortMaster) — export with official Godot 4.5.1 .NET templates for linux-arm64
- **Performance target**: warm 60 fps median per zone; measure frame-time distributions on the device, not just averages. Unshaded vertex colours, ≤64 resident mesh nodes, no shadows. See `docs/INSTRUMENTATION.md` for exact metrics and `docs/KNOWN-ISSUES.md` for hitches and cold-run caveats.
- **Build goal**: a reproducible local ARM64 export and flat PortMaster ZIP; see `README.md`.

## Architecture rules (non-negotiable)

- Kit/Game split: `Kit/` = zero game knowledge (grep must not find `VerdantCrown` in Kit/)
- C# only, `public partial class`, signals via `[Signal]` + `SignalName`
- Data-driven entities (JSON records), FSMs for behaviour, telemetry fields added before features grow
- `dotnet build` after every C# change; rerun `tools/deploy-r36s.sh` to test a fresh device export (old binaries are not evidence for a failed build).


## Asset layout (convention, 2026-09-23)

ONE asset root: `Assets/` — with `Assets/Models/` (Blender/GLB track), and future `Assets/Audio/`,
`Assets/Backgrounds/`, `Assets/Sprites/`. **Never create a lowercase `assets/` twin**: Linux keeps
case-variant siblings side by side, so a second half-empty tree appears with no error and no
references. (An empty `assets/{audio,backgrounds,sprites}` scaffold was found and removed this
day.) Generated outputs stay out of git: `build451/`, `bin/`, `obj/`, and the staged port payload
see `.gitignore`.

## Data layout (convention, 2026-09-23)

`Data/` = CONTENT only (the JSON the game reads at `res://Data/*.json`). Data CODE lives in
`Scripts/Data/` (the `VerdantCrown.Data` records — one per schema entity — and the `GameData`
loader). Directory placement does not affect the namespace, so this split costs nothing and
keeps code under `Scripts/` and content under `Data/`. (An empty
`Scripts/Data/` had existed since an early scaffold mkdir — git cannot track empty dirs, which
is why it read as a mystery second data dir; it now holds what its name promised.)
