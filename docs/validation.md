# Validation evidence

Validation date: 2026-10-01, Linux x64, .NET SDK 10.0.201, Release build.
Core: vendored Essgee revision `16e8ffdf1b44f00fbf21d2d97130c260e887dff2` (Z80, VDP,
PSG, mappers) and a C# port of emu2413 v1.5.9 (YM2413). No native dependency.

## Why the managed core

An intermediate iteration replaced Essgee with a patched native Gearsystem process
because GG Sonic 1 v1.1 appeared to stall at the Sega splash screen and Essgee lacks
a YM2413. Re-testing showed the stall was not an Essgee limitation: with the same
ROM, the managed core reaches the title screen, the Green Hill map after Start, and
gameplay with Sonic moving and rings counting. The FM gap is closed by the emu2413
port below. The native backend was removed to keep the server dependency-free and
portable to Windows.

## Automated tests

```bash
dotnet test sms-debug-mcp.slnx -c Release -m:4
```

**52 tests** pass with none skipped. They drive generated, redistributable ROM bytes
through the real Z80, VDP, PSG and YM2413 rather than test doubles, plus one outer
process speaking MCP `initialize`, `tools/list` and `tools/call` over stdio.
Assertions cover memory/register changes, pixels, WAV data, noise/stereo routing,
banking, traps, input, bounded execution and identical sub-frame replay, and:

- **YM2413 bit-exactness**: the C# port renders a register script (ROM instruments,
  user patch with feedback, rhythm mode, key-off/sustain) to the same SHA-256 as the
  C emu2413 v1.5.9. During development a longer 140,000-sample script with 300 random
  register writes and the test-mode register was also identical at NTSC and PAL clocks.
- **FM detection and mixer**: port F2 reads back `0xF9` after writing 1; control
  values 0..3 select PSG, FM, silence and both; an FM violin note at F-number 384,
  block 2 decodes as 145.6 Hz and is audible with every PSG channel silenced.
- **Configuration**: `fm: false` and Game Gear sessions have no FM ports; snapshots
  replay identical FM audio and are rejected by a session with a different FM unit.
- **Tracing and muting**: FM port writes are traced with their writer; muting an FM
  voice silences it.

## Real-ROM stdio qualification

`scripts/qualify_roms.py --gameplay` drives the built server through stdio. Each ROM:

1. Traces YM2413 writes over 180 frames (SMS), then resets.
2. 180 observed frames with 10 correlated CPU/RAM/VDP/PSG/YM2413/screen samples.
3. A PNG decoded and validated for SMS/GG dimensions.
4. 30-frame WAV capture and a snapshot restore, with byte-identical repeated audio
   and equal CPU/VDP state.
5. A Start/right/button timeline, a tileset PNG and a breakpoint at the current PC,
   verified to stop before execution.
6. A separate title → start → start → move sequence, saved as a screenshot.

All 17 ROMs passed every step. The server listed **51 tools**.

| System | ROM | Distinct screens | 30-frame WAV peak | FM writes |
| --- | --- | --- | --- | --- |
| GG | Sonic the Hedgehog (World) (v1.1) | 9 | 8191 | — |
| GG | Sonic the Hedgehog 2 (World) | 3 | 24575 | — |
| GG | Sonic Chaos (USA, Europe) | 4 | 4875 | — |
| GG | The Simpsons: Bart vs. the Space Mutants | 3 | 4095 | — |
| GG | The Simpsons: Bart vs. the World | 2 | 7740 | — |
| SMS | Aladdin (Europe) | 5 | 3603 | 1 (probe only) |
| SMS | Alex Kidd in Miracle World (USA, Europe) (v1.1) | 3 | 9894 | 0 |
| SMS | Bubble Bobble (Europe) | 2 | 0 (silent interval) | 0 |
| SMS | Fantasy Zone (World) (v1.2) | 3 | 5830 | 0 |
| SMS | Ghouls'n Ghosts (USA, Europe) | 9 | 4361 | 0 |
| SMS | Golden Axe (USA, Europe) | 2 | 14693 | 0 |
| SMS | Sonic the Hedgehog (USA, Europe) | 4 | 14554 | 0 |
| SMS | Wonder Boy in Monster Land (USA, Europe) | 3 | 0 (silent interval) | 1148 |
| SMS | Four local Sonic builds/hacks | 2–6 | 11836–18512 | 0 |

The gameplay sequence presses Start on Game Gear and button 1 on SMS (SMS Start is
the Pause NMI). Its screenshots show in-game play, with sprites and HUD, for GG
Sonic 1 (Green Hill) and Sonic 2, and for SMS Alex Kidd, Bubble Bobble, Fantasy
Zone, Ghouls'n Ghosts, Golden Axe, Wonder Boy in Monster Land, Sonic the Hedgehog
and three of the four other Sonic builds. The remaining titles stop at menus or
player select because the generic button sequence does not navigate them, which is
not a compatibility failure.

### YM2413 with a commercial game

Wonder Boy in Monster Land (export release) detects the YM2413, selects FM-only
output (`F2 = 1`) and plays its soundtrack through FM: after 900 frames,
`read_fm_state` shows piano, electric guitar, organ and piano voices at 164, 109,
109 and 218 Hz. A 600-frame capture has RMS 0.076 with FM versus 0.111 for the PSG
soundtrack of an `fm: false` session; both WAVs are kept locally for listening in
`artifacts/qualification-fm/`.

## Performance

Release build, Wonder Boy in Monster Land, 1,200 frames through `run_frame`:
**280 fps** without FM and **232 fps** with the YM2413, about 4× real time, so a
600-frame request finishes in under 3 seconds, well within the 15-second limit.

### Run-loop overhead

`benchmarks/Sms.Debug.Benchmarks` runs 300 frames of a generated ROM that streams 64
VRAM bytes and updates 256 RAM bytes per loop. Figures are steady state, after one
warm-up frame. Measured on 2026-10-04 on a Linux x64 machine at load 8–9 on 8 cores,
alternating runs of both builds:

```bash
dotnet run -c Release --project benchmarks/Sms.Debug.Benchmarks -- 300
```

| Scenario | Before: ms/frame | Before: B/instr | After: ms/frame | After: B/instr |
| --- | ---: | ---: | ---: | ---: |
| No watchpoints | 2.9–3.2 | 296 | 2.0–2.1 | 0.07 |
| 10 watchpoints and a breakpoint, none hit | 3.5–4.0 | 296 | 2.1–2.2 | 0.08 |
| Run to `SCANLINE == 27` | 3.5–4.4 | 512 | 2.0–2.1 | 0.53 |
| RetroSharp frame loop, stopping on each VRAM write | 3.7 | 331 | 2.1–2.3 | 20 |
| The same loop with `TraceWritesUntilScanline` | — | — | 2.2–2.3 | 7 |

In the last two rows, the remaining allocations are the `BusAccess` records each
watched write returns. Once the run loop is cheap, the VDP's per-pixel Mode 4
background renderer takes more than half of the time.

Reports with ROM hashes, PNGs and WAVs are in local `artifacts/qualification-*`
folders, which are ignored by Git and excluded from packages. No commercial ROMs
were modified or copied into this repository.

Passing these workflows does not certify full game compatibility, collision or
gameplay correctness, analog audio fidelity or cycle accuracy.
