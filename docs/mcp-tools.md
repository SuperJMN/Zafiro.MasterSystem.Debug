# MCP tool reference

Tools use the same paused-machine workflow as the sibling Game Boy and NES servers.
All requests are serialized, including reads. Tool errors use MCP `isError: true`
with `{ "error": { "code": "...", "message": "..." } }` text content.
Successful structured results are JSON text; graphics are standard MCP PNG image blocks.
Byte arrays in JSON are base64; memory reads also provide a hexadecimal string.

## Tool inventory (51)

| Workflow | Tool names |
| --- | --- |
| ROM/state | `load_rom`, `get_state`, `reset`, `save_state`, `load_state` |
| Execution | `step_instruction`, `step_over`, `step_out`, `continue_until_break`, `run_until_condition`, `run_frame` |
| Breakpoints | `set_breakpoint`, `clear_breakpoint`, `list_breakpoints` |
| Watchpoints | `set_watchpoint`, `clear_watchpoint`, `list_watchpoints` |
| CPU/bus | `read_registers`, `read_memory`, `write_memory`, `read_io_port`, `write_io_port`, `disassemble` |
| Input | `set_controller`, `set_joypad`, `press_buttons`, `run_input_timeline` |
| Graphics | `read_vdp_state`, `read_ppu_state`, `capture_screen`, `read_screen_region`, `dump_palette`, `dump_sprites`, `dump_oam`, `dump_tilemap`, `capture_tilemap`, `dump_tileset` |
| Observation/trace | `observe_execution`, `observe_screen`, `trace_writes`, `trace_video_writes`, `trace_psg_writes`, `trace_fm_writes`, `find_last_writer` |
| Sound | `read_psg_state`, `read_fm_state`, `set_audio_channel`, `capture_audio` |
| Symbols | `load_symbols`, `resolve_symbol`, `read_symbol` |

`read_ppu_state`, `dump_oam`, `set_joypad` and `observe_screen` are compatibility aliases.
On Sega hardware the video processor is a **VDP** and sprites live in a VRAM SAT.
`tools/list` from the running server is the authoritative parameter/schema inventory.

## Addresses, memory and I/O

Address arguments are strings: `"49152"`, `"0xC000"` or `"$C000"`.

| Space | Range | Meaning |
| --- | --- | --- |
| `cpu` | 0..65535 | Current ROM/RAM mapping; Sega mapper registers at FFFC..FFFF |
| `ram` | 0..8191 | Physical work RAM, mirrored at C000..DFFF and E000..FFFF |
| `vram` | 0..16383 | VDP pattern/name/sprite RAM |
| `cram` | 0..31 SMS, 0..63 GG | Hardware color bytes |
| `vdp` | 0..10 | VDP registers; direct writes apply resolution changes |
| `rom` | 0..ROM size−1 | Unmapped cartridge bytes, read-only |
| `cartram` | 0..32767 | Physical cartridge RAM |

Memory reads are side-effect-free. A CPU memory write follows hardware rules,
including ignored ROM writes, ROM bank changes and cartridge RAM enabling.
Debugger writes are excluded from watchpoint hits and writer history.
`read_io_port` performs a hardware read and may acknowledge VDP interrupts or
advance the data buffer; it is explicitly **not** a read-only tool.

```json
{"tool":"load_rom","arguments":{"path":"/path/game.sms","mapper":"sega","standard":"ntsc","fm":true}}
{"tool":"read_memory","arguments":{"address":"0xC000","length":64}}
{"tool":"write_memory","arguments":{"address":"0xC000","hex":"01 02 FF"}}
{"tool":"read_memory","arguments":{"address":"0","length":32,"space":"cram"}}
```

These examples use `{tool, arguments}` for readability; actual calls use MCP
`tools/call` with `{name, arguments}`.

## Execution and traps

`set_breakpoint` takes `address`, optional `condition` and optional `bank`.
Execution stops **before** the matching instruction. A subsequent continue skips
the breakpoint just hit once so it can make progress. `step_instruction` ignores
execution breakpoints but honors watchpoints. Breakpoints without a bank apply
to every mapping of that CPU address.

`set_watchpoint` takes `address`, `length` (default 1), `space` and `access`.
CPU/I/O support `read`, `write` or `readwrite`; direct `vram`, `cram` and `vdp`
spaces support writes. CPU reads include instruction fetches. Watchpoints stop
after the touching instruction/interrupt entry and report its PC, bank and access.
Use I/O watchpoints to observe hardware VRAM reads through the VDP data port.

Conditions are one comparison with a numeric value: `==`, `!=`, `<`, `<=`, `>`, `>=`.
Operands include Z80 register names, `[HL]`/`[address]`, `FRAME`, `SCANLINE`,
`VCOUNTER`, and `HCOUNTER`. Conditions do not perform side-effectful I/O reads.

```json
{"tool":"set_breakpoint","arguments":{"address":"0x1234","condition":"A == 0x10","bank":2}}
{"tool":"set_watchpoint","arguments":{"address":"0xC000","length":16,"access":"write"}}
{"tool":"set_watchpoint","arguments":{"address":"0xBF","space":"io","access":"write"}}
{"tool":"continue_until_break","arguments":{"maxInstructions":100000,"maxCycles":1000000}}
{"tool":"run_until_condition","arguments":{"condition":"[0xC000] >= 10"}}
```

Run results contain `reason`, actual instructions/cycles/frames executed,
breakpoint/watchpoint IDs, the touching access and final state. Stop reasons are
`breakpoint`, `watchpoint`, `condition`, `frame_complete`, `instruction_limit`,
`cycle_limit`, `time_limit` and `cancelled`.
The instruction counter includes interrupt entry and HALT idle steps.
The cycle limit is checked between instructions, so the final instruction can
cross it by its own cycle count. `step_over` recognizes CALL/conditional CALL/RST;
`step_out` uses the return address at SP and stack depth, so use it at a call-frame boundary.

## Input and correlated observation

Buttons: `up down left right 1 2`, with `a/b` and `b1/b2` aliases.
Player 1 `pause`/`start` triggers SMS Pause NMI on a rising edge, or GG Start.
SMS `reset` holds the hardware reset-button bit; `reset` tool resets the emulator.
Game Gear does not implement a second physical controller.

```json
{"tool":"run_input_timeline","arguments":{"segments":[{"frames":30,"buttons":"right","player":1},{"frames":4,"buttons":"right 1","player":1},{"frames":20,"buttons":"","player":1}]}}
{"tool":"observe_execution","arguments":{"frames":120,"sampleEvery":4,"addresses":[49152,49153]}}
```

Each observation contains CPU, VDP, PSG and (when present) YM2413 state, requested one-byte CPU probes,
completed frame number and PNG SHA-256. Sampling stops when execution stops.
Input timelines validate every segment before running, stop on debug traps, and
release controls on completion/interruption unless `release` is false.

`trace_writes` selects `cpu`, `io`, `vram`, `cram` or `vdp`.
Traces contain **instruction-start** timestamps, rather than exact bus-cycle timestamps.
`trace_video_writes` selects VRAM. `trace_psg_writes` and `trace_fm_writes` (ports
F0..F2) filter before applying the entry limit. Traces continue executing after reaching that limit and report
`truncated: true`. `find_last_writer` only knows writes seen since load/reset/restore.

## Graphics and sound

`capture_screen` returns the paused active image without running a frame.
`read_screen_region` returns RGB24 integers. `dump_tileset` accepts `start`, `count`
and `palette`; `capture_tilemap` renders the entire unscrolled Mode 4 name table.
`dump_tilemap` includes palette, flips and priority. `dump_sprites` includes all
64 SAT entries, flagging terminators and entries after them; it does not claim
every listed sprite is currently visible.

```json
{"tool":"capture_screen","arguments":{}}
{"tool":"dump_tileset","arguments":{"start":0,"count":256,"palette":1}}
{"tool":"capture_tilemap","arguments":{}}
{"tool":"read_psg_state","arguments":{}}
{"tool":"read_fm_state","arguments":{}}
{"tool":"set_audio_channel","arguments":{"channel":"4","enabled":false}}
{"tool":"set_audio_channel","arguments":{"chip":"fm","channel":"bd","enabled":false}}
{"tool":"capture_audio","arguments":{"frames":120,"path":"captures/sound.wav"}}
```

`capture_audio` runs the machine and records samples from that execution only.
It returns WAV base64 (unless saved to `path`), sample-value count, RMS normalized
to 32768, peak absolute sample value, and execution result. SMS is mono; GG
has interleaved left/right channels. `sampleValues` counts both GG channels;
divide by two for sample frames. Muting affects subsequent mixed captures.
The PSG's raw waveform retains DC; no analog reconstruction is promised.

### YM2413 (FM)

SMS sessions load with `fm: true` by default, modelling a Japanese Master System
(or Mark III with FM unit). `fm: false` models an export console: the ports are
not decoded and games fall back to PSG music. Game Gear never has the chip.
`get_state` reports `fmUnit`.

| Port | Write | Read |
| --- | --- | --- |
| `F0` | Register address latch | Controller port (unchanged) |
| `F1` | Register data | Controller port (unchanged) |
| `F2` | Audio control, bits 0-1 | `0xF8` \| latched bit 0, for FM detection |

Audio control follows the Japanese SMS mixer: `0` PSG only (power-on), `1` FM
only, `2` silent, `3` both. The synthesizer is a C# port of
[emu2413](https://github.com/digital-sound-antiques/emu2413) v1.5.9, verified
sample-for-sample against the C original. It runs at the Z80 clock and is resampled
to 44.1 kHz in phase with the PSG; FM is mixed at four times its raw level so FM and
PSG soundtracks have comparable loudness. `read_fm_state` decodes the 64 registers:
per-channel instrument (ROM name), attenuation, F-number, block, key/sustain and
frequency, plus rhythm mode, keyed rhythm voices and muted voices.
`set_audio_channel` with `chip: "fm"` mutes melody channels `1`..`9` or rhythm
voices `bd`, `sd`, `tom`, `cym`, `hh`.

PNG/WAV/snapshot artifacts require new relative `.png`/`.wav`/`.smsstate` paths
under the server working directory. Parent traversal and symlinks are rejected;
existing artifacts are never overwritten. Omit `path` for inline output.

## Snapshots and symbols

`save_state` returns `stateBase64` or creates a `.smsstate` artifact.
`load_state` accepts exactly one of `path` or `stateBase64` and requires a matching
ROM SHA-256, mapper, system, FM unit, standard and snapshot version. Restore constructs a
replacement machine before switching the active session. Snapshots include
private chip latches/phases (including the YM2413 envelopes, phases and resampler), pending interrupts, mapper state, RAM, input,
sprite-evaluation buffers and partial audio/video, so sub-frame replay is deterministic.
They are backend-version-specific, not portable emulator savestates.

`load_symbols` reads `ADDR Name` or `BANK:ADDR Name` with hexadecimal addresses,
`;`/`#` comments and at most 4 MiB. `resolve_symbol` returns the descriptor;
`read_symbol` rejects a symbol whose bank is not currently mapped.

## Bounds

| Operation | Bound |
| --- | --- |
| ROM | 1 KiB..4 MiB, plus optional copier header |
| Single memory request | 1..65536 bytes within the selected space |
| Instruction stepping | 1..10000 steps |
| Continue/step over/out | <=5 million steps; default 1 million |
| Run frame / observation / audio | 1..600 frames |
| Frame execution | <=5 million steps, <=500 million cycles per run |
| Time | 15 seconds per underlying run; cancellation checked every 1024 steps |
| Timeline | 1..120 segments, <=600 total frames |
| Observation probes | <=32 CPU addresses |
| Trace output | 1..16384 entries; explicit truncation |
| Disassembly | 1..256 instructions |
| Snapshot input | <=8 MiB |

All frame workflows report actual completion; hitting a bound or trap is never
reported as successful full execution of the requested interval.
