# Sms.Mcp

A .NET 10 Sega Master System / Game Gear debugging MCP, in the style of the sibling
Game Boy and NES servers. It runs headlessly over stdio and is **fully managed**:
no native libraries, emulator executables, SDL, OpenGL or BIOS are required, so the
same build runs on Linux, Windows and macOS. The server exposes **51 tools** for
Z80 debugging, conditional breakpoints, watchpoints, correlated observation,
graphics and PSG/YM2413 audio capture.

The emulation core is vendored C#: the Z80, VDP and SN76489 PSG come from
[Essgee](https://github.com/xdanieldzd/Essgee), and the YM2413 FM synthesizer is a
C# port of [emu2413](https://github.com/digital-sound-antiques/emu2413) v1.5.9 that
matches the C original sample for sample. Both are MIT licensed.

## Build and run

Requires the .NET 10 SDK.

```bash
dotnet build sms-debug-mcp.slnx -c Release -m:4
dotnet src/Sms.Debug.Mcp/bin/Release/net10.0/Sms.Mcp.dll
```

Diagnostics use stderr exclusively. Every request is serialized, and execution
always returns to a paused machine, reporting actual frames, instructions, cycles
and stop reason.

## MCP configuration

After building, use an absolute path:

```json
{
  "mcpServers": {
    "sms_debug": {
      "command": "dotnet",
      "args": ["/home/jmn/Repos/SmsMcp/src/Sms.Debug.Mcp/bin/Release/net10.0/Sms.Mcp.dll"]
    }
  }
}
```

## Capabilities

| Area | Behavior |
| --- | --- |
| Systems | SMS (NTSC/PAL) and Game Gear; Sega and Codemasters mappers; ZIPs with one ROM |
| CPU | Z80 primary/alternate registers, disassembly, step/over/out, bounded continue |
| Traps | Execution breakpoints with conditions and ROM bank; CPU/I/O/VRAM/CRAM/VDP watchpoints |
| State and memory | Side-effect-free reads of CPU/RAM/VRAM/CRAM/VDP/ROM/cartridge RAM, writes, I/O, snapshots, symbols |
| Observation | Correlated CPU/RAM probes/VDP/PSG/YM2413/frame/screen hashes; write traces with PC, bank, cycle and scanline; last writer |
| Graphics | Screen PNG, screen regions, Mode 4 tileset and name-table PNGs, decoded tilemap, sprites and palette |
| Sound | PSG and YM2413 inspection, per-voice muting, mixed PCM16 WAV at 44.1 kHz (mono SMS, stereo GG) |
| Input | Controllers, SMS Pause/Reset, GG Start, deterministic frame timelines with guaranteed release |

SMS sessions attach a YM2413 by default, like a Japanese Master System; load with
`fm: false` to model an export console. Game Gear has no FM chip. See
[tool schemas and examples](docs/mcp-tools.md); `tools/list` is authoritative.

## Validation

```bash
dotnet test sms-debug-mcp.slnx -c Release -m:4
python3 scripts/qualify_roms.py --gameplay "/path/to/roms/"*.zip
```

**37 tests** pass, including a real stdio session, FM detection and mixer behavior,
FM snapshots, and a bit-exact comparison of the YM2413 port against the C original.
Seventeen local ROMs passed the stdio debugger workflow, and twelve of them reached
in-game play in a scripted input sequence, including GG Sonic 1 v1.1. Wonder Boy in
Monster Land detects the YM2413 and plays its FM soundtrack. See [evidence](docs/validation.md). ROMs and their graphics/audio
artifacts remain local and are not redistributed.

## Packaging

```bash
dotnet pack src/Sms.Debug.Mcp/Sms.Debug.Mcp.csproj -c Release -o artifacts/packages
dotnet tool install Sms.Mcp --tool-path artifacts/tool --add-source artifacts/packages --version 0.3.0
artifacts/tool/smsmcp
```

No package has been published remotely.

## Scope

The tests prove the described debugger workflows, not complete game compatibility
or bus-cycle accuracy. Traces carry instruction-start timestamps. Snapshots are
backend-version-specific rather than portable savestates. WAV capture exports
samples and does not play them through speakers. SG-1000, SC-3000, Korean mappers
and peripherals such as the light phaser or paddle are not emulated.

## License

MIT. Vendored Essgee and emu2413 code keep their MIT notices; see
[third-party notices](THIRD-PARTY-NOTICES.md).
