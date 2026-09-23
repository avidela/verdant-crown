# Verdant Crown — PortMaster package

A playable 3D C# / Godot 4.5.1 .NET action-adventure for the R36S (ArkOS, ARM64, 640×480). One overworld, five dungeon rooms, a boss and a victory ending. No audio yet.

## Controls

| R36S | Action |
|---|---|
| Left stick or d-pad | Move |
| A | Attack |
| B | Dodge |
| Start | Pause |
| L1 | Save a screenshot |
| Select+Start | Quit |

Face buttons and d-pad go through the shipped `controls.gptk` keyboard mapping; left-stick axes go directly to Godot. This avoids a tested kernel-label swap between face buttons. Right-stick camera movement is not implemented.

## Installing a built ZIP

Extract **both** `Verdant Crown.sh` and `verdant-crown/` from `verdant-crown.zip` into the ArkOS card's `EASYROMS/ports/` folder. They must be **side by side**; don't copy the repository's `ports/` directory onto the card. Restart EmulationStation or reboot. The handheld needs a working PortMaster and its `weston_pkg_0.2.squashfs` runtime (PortMaster can download it if the device is online). If it does not launch, inspect `/roms/ports/verdant-crown/log.txt`.

## Building from source

See the repository's root `README.md` for the complete Linux prerequisites, editor/template download, offline packaging, optional SSH installation and runtime pre-staging instructions. In short, install **official Godot 4.5.1 .NET** with matching `4.5.1.stable.mono` export templates plus .NET SDK 8+, then run `tools/deploy-r36s.sh` and `tools/install-port.sh` from the repository root. The first script asserts ARM64 player + .NET payload + compatible glibc; the second creates a flat `build451/verdant-crown.zip` without needing a device. Use `GODOT_BIN=/path/to/editor` if the editor is not named `godot` on your PATH.

Why that engine version? A tested newer player demanded glibc 2.35+, but the target ArkOS device has 2.30; that binary fails before game startup. The 4.5.1 player requires at most glibc 2.28. The exported game carries its own ARM64 .NET runtime — no separate .NET installation on the R36S.

Original game code and assets are MIT-licensed; the game ZIP contains the license in `verdant-crown/licenses/`. Godot and the bundled .NET runtime retain their own licenses and third-party notices, also shipped in that directory. The source repository's `docs/KNOWN-ISSUES.md` lists open game/device issues.
