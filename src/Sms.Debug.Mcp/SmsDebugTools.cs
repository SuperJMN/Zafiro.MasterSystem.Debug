using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sms.Debug.Core;
using Sms.Debug.Emulator;

namespace Sms.Debug.Mcp;

[McpServerToolType]
public static class SmsDebugTools
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // All calls, including images and snapshots, see an atomic paused machine state.
    private static CallToolResult Call(SmsDebugSession session, Func<object?> operation)
    {
        lock (session.SyncRoot)
        {
            try
            {
                var result = operation();
                if (result is ContentBlock content) return new() { Content = [content] };
                return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, Json) }] };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or FormatException or OverflowException or JsonException or NotSupportedException
                or KeyNotFoundException or Essgee.Exceptions.EmulationException)
            {
                return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(
                    new { Error = new { Code = ex is InvalidOperationException ? "invalid_state" : ex is NotSupportedException ? "not_supported" : "invalid_request", Message = ex.Message } }, Json) }] };
            }
        }
    }

    private static int Address(string text) => Condition.Number(text);
    private static byte[] HexBytes(string text) => Convert.FromHexString(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
    private static object Png(byte[] bytes, string? path) => path == null ? ImageContentBlock.FromBytes(bytes, "image/png") : Artifacts.Write(path, ".png", bytes);

    [McpServerTool(Name = "load_rom", Destructive = false)]
    [Description("Loads a local .sms/.gg ROM or ZIP with one ROM. Mapper: sega or codemasters; standard: ntsc or pal; system: auto/sms/gg. fm (SMS only, default true) attaches a YM2413 like a Japanese SMS; false emulates an export console. Resets debug configuration. Game Gear uses NTSC.")]
    public static CallToolResult LoadRom(SmsDebugSession session, string path, string mapper = "sega", string standard = "ntsc", string system = "auto", bool fm = true)
        => Call(session, () => session.LoadRom(path, mapper, standard, system, fm));

    [McpServerTool(Name = "get_state", ReadOnly = true, Destructive = false)]
    [Description("Reads ROM identity, bank mapper, timing, FM unit presence, frame/cycle/instruction counters, Z80 registers and VDP state without running the machine.")]
    public static CallToolResult GetState(SmsDebugSession session) => Call(session, session.GetState);

    [McpServerTool(Name = "reset", Destructive = false)]
    [Description("Deterministically resets CPU, VDP, PSG, YM2413 and banks. Preserves cartridge RAM and breakpoints/watchpoints; clears writer history.")]
    public static CallToolResult Reset(SmsDebugSession session) => Call(session, session.Reset);

    [McpServerTool(Name = "read_registers", ReadOnly = true, Destructive = false)]
    [Description("Reads Z80 primary/alternate registers, interrupt flip-flops, mode and HALT state.")]
    public static CallToolResult ReadRegisters(SmsDebugSession session) => Call(session, session.ReadRegisters);

    [McpServerTool(Name = "step_instruction", Destructive = false)]
    [Description("Executes 1..10000 instruction/interrupt steps. Ignores execution breakpoints; honors watchpoints. Advances VDP, PSG and YM2413 with the CPU.")]
    public static CallToolResult StepInstruction(SmsDebugSession session, int count = 1, CancellationToken cancellationToken = default)
        => Call(session, () => session.StepInstruction(count, cancellationToken));

    [McpServerTool(Name = "step_over", Destructive = false)]
    [Description("Steps over Z80 CALL/conditional CALL/RST using return PC and stack depth; honors breakpoints/watchpoints and execution limits.")]
    public static CallToolResult StepOver(SmsDebugSession session, int maxInstructions = 1000000, CancellationToken cancellationToken = default)
        => Call(session, () => session.StepOver(maxInstructions, cancellationToken));

    [McpServerTool(Name = "step_out", Destructive = false)]
    [Description("Runs to the return address at SP and the restored stack depth; bounded to maxInstructions or 20 million cycles.")]
    public static CallToolResult StepOut(SmsDebugSession session, int maxInstructions = 1000000, CancellationToken cancellationToken = default)
        => Call(session, () => session.StepOut(maxInstructions, cancellationToken));

    [McpServerTool(Name = "continue_until_break", Destructive = false)]
    [Description("Runs until a breakpoint/watchpoint or explicit instruction/cycle/time/cancellation limit. Continues past a breakpoint just hit once.")]
    public static CallToolResult ContinueUntilBreak(SmsDebugSession session, int maxInstructions = 1000000, long maxCycles = 20000000, CancellationToken cancellationToken = default)
        => Call(session, () => session.Continue(maxInstructions, maxCycles, cancellationToken));

    [McpServerTool(Name = "run_until_condition", Destructive = false)]
    [Description("Runs to a comparison such as A == 0x10, PC == 0x1234, [HL] != 0, [0xC000] >= 2, SCANLINE > 192 or FRAME >= 60. Supports == != < <= > >=.")]
    public static CallToolResult RunUntilCondition(SmsDebugSession session, string condition, int maxInstructions = 1000000, CancellationToken cancellationToken = default)
        => Call(session, () => session.RunUntilCondition(condition, maxInstructions, cancellationToken));

    [McpServerTool(Name = "run_frame", Destructive = false)]
    [Description("Advances 1..600 completed VDP frames, stopping early on breakpoints/watchpoints or bounds. Reports actual completed frames and stop reason.")]
    public static CallToolResult RunFrame(SmsDebugSession session, int frames = 1, CancellationToken cancellationToken = default)
        => Call(session, () => session.RunFrame(frames, cancellationToken));

    [McpServerTool(Name = "set_breakpoint", Destructive = false)]
    [Description("Adds an execution breakpoint at a CPU address (decimal, 0xHEX or $HEX), optionally conditioned and qualified by ROM bank 0..255.")]
    public static CallToolResult SetBreakpoint(SmsDebugSession session, string address, string? condition = null, int? bank = null)
        => Call(session, () => session.SetBreakpoint(Address(address), condition, bank));

    [McpServerTool(Name = "clear_breakpoint", Destructive = false)]
    [Description("Removes the breakpoint with the given ID.")]
    public static CallToolResult ClearBreakpoint(SmsDebugSession session, int id) => Call(session, () => new { Removed = session.ClearBreakpoint(id) });

    [McpServerTool(Name = "list_breakpoints", ReadOnly = true, Destructive = false)]
    [Description("Lists configured execution breakpoints.")]
    public static CallToolResult ListBreakpoints(SmsDebugSession session) => Call(session, session.ListBreakpoints);

    [McpServerTool(Name = "set_watchpoint", Destructive = false)]
    [Description("Watches an address or range in cpu/io/vram/cram/vdp space. Access: read/write/readwrite; VDP direct spaces support writes. Stops after the touching instruction, including opcode fetches for CPU reads.")]
    public static CallToolResult SetWatchpoint(SmsDebugSession session, string address, int length = 1, string space = "cpu", string access = "write")
        => Call(session, () => session.SetWatchpoint(Address(address), length, space, access));

    [McpServerTool(Name = "clear_watchpoint", Destructive = false)]
    [Description("Removes a watchpoint by ID.")]
    public static CallToolResult ClearWatchpoint(SmsDebugSession session, int id) => Call(session, () => new { Removed = session.ClearWatchpoint(id) });

    [McpServerTool(Name = "list_watchpoints", ReadOnly = true, Destructive = false)]
    [Description("Lists configured memory and I/O watchpoints.")]
    public static CallToolResult ListWatchpoints(SmsDebugSession session) => Call(session, session.ListWatchpoints);

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false)]
    [Description("Reads up to 65536 bytes without bus side effects from cpu/ram/vram/cram/vdp/rom/cartram. Returns hex and base64 bytes. CPU view follows current banks; ram is physical 8 KiB work RAM.")]
    public static CallToolResult ReadMemory(SmsDebugSession session, string address, int length = 64, string space = "cpu")
        => Call(session, () => session.ReadMemory(Address(address), length, space));

    [McpServerTool(Name = "write_memory", Destructive = false)]
    [Description("Writes hex bytes into cpu/ram/vram/cram/vdp/cartram. CPU writes use hardware mapping (ROM writes may be ignored or switch banks). Direct debugger writes do not trigger watchpoints/writer history.")]
    public static CallToolResult WriteMemory(SmsDebugSession session, string address, string hex, string space = "cpu")
        => Call(session, () => session.WriteMemory(Address(address), HexBytes(hex), space));

    [McpServerTool(Name = "read_io_port", Destructive = false)]
    [Description("Performs a hardware I/O read. Reading VDP status acknowledges interrupts; reading VDP data advances its address/read buffer. For side-effect-free inspection use read_vdp_state/read_memory.")]
    public static CallToolResult ReadIoPort(SmsDebugSession session, string port)
        => Call(session, () => new { Port = Address(port), Value = session.ReadIoPort(Address(port)) });

    [McpServerTool(Name = "write_io_port", Destructive = false)]
    [Description("Writes an 8-bit hardware I/O port/value, including VDP control/data, PSG, YM2413 (F0 address, F1 data, F2 audio control), memory/I/O control and Game Gear stereo.")]
    public static CallToolResult WriteIoPort(SmsDebugSession session, string port, int value)
        => Call(session, () => { session.WriteIoPort(Address(port), value); return session.GetState(); });

    [McpServerTool(Name = "disassemble", ReadOnly = true, Destructive = false)]
    [Description("Disassembles 1..256 Z80 instructions in the current CPU mapping, including CB/ED/DD/FD prefixes, bytes and bank. Undocumented mnemonic coverage follows Essgee.")]
    public static CallToolResult Disassemble(SmsDebugSession session, string address, int count = 16)
        => Call(session, () => session.Disassemble(Address(address), count));

    [McpServerTool(Name = "set_controller", Destructive = false)]
    [Description("Sets held buttons for player 1/2: up down left right 1 2; aliases a/b, b1/b2. Player 1 pause/start produces SMS NMI or GG Start; reset is SMS hardware Reset. Empty releases all buttons for that player.")]
    public static CallToolResult SetController(SmsDebugSession session, string buttons = "", int player = 1)
        => Call(session, () => { session.SetController(buttons, player); return session.GetState(); });

    [McpServerTool(Name = "set_joypad", Destructive = false)]
    [Description("Alias of set_controller, for workflows shared with Game Boy/NES MCPs.")]
    public static CallToolResult SetJoypad(SmsDebugSession session, string buttons = "", int player = 1) => SetController(session, buttons, player);

    [McpServerTool(Name = "press_buttons", Destructive = false)]
    [Description("Holds buttons for a bounded number of frames, then releases them even if interrupted by a breakpoint.")]
    public static CallToolResult PressButtons(SmsDebugSession session, string buttons, int frames = 1, int player = 1, CancellationToken cancellationToken = default)
        => Call(session, () => session.RunInputTimeline([new(frames, buttons, player)], cancellationToken: cancellationToken));

    [McpServerTool(Name = "run_input_timeline", Destructive = false)]
    [Description("Executes deterministic {frames, buttons, player} segments. 1..120 segments totaling <=600 frames; stops on debug traps and releases controls by default.")]
    public static CallToolResult RunInputTimeline(SmsDebugSession session, InputSegment[] segments, bool release = true, CancellationToken cancellationToken = default)
        => Call(session, () => session.RunInputTimeline(segments, release, cancellationToken));

    [McpServerTool(Name = "read_vdp_state", ReadOnly = true, Destructive = false)]
    [Description("Inspects VDP registers/status/scanline/counters, table bases, address/code/control latch, mode, display and pending interrupts without acknowledgement.")]
    public static CallToolResult ReadVdpState(SmsDebugSession session) => Call(session, session.ReadVdpState);

    [McpServerTool(Name = "read_ppu_state", ReadOnly = true, Destructive = false)]
    [Description("Compatibility alias for read_vdp_state.")]
    public static CallToolResult ReadPpuState(SmsDebugSession session) => ReadVdpState(session);

    [McpServerTool(Name = "capture_screen", ReadOnly = true, Destructive = false)]
    [Description("Returns the paused active screen as an inline PNG (SMS 256x192/224/240, GG 160x144). Optional new relative .png artifact path under server working directory.")]
    public static CallToolResult CaptureScreen(SmsDebugSession session, string? path = null)
        => Call(session, () => Png(session.CaptureScreen(), path));

    [McpServerTool(Name = "read_screen_region", ReadOnly = true, Destructive = false)]
    [Description("Reads a bounded active-screen rectangle as RGB24 integer pixels without executing.")]
    public static CallToolResult ReadScreenRegion(SmsDebugSession session, int x, int y, int width, int height)
        => Call(session, () => new { X = x, Y = y, Width = width, Height = height, Pixels = session.ReadScreenRegion(x, y, width, height) });

    [McpServerTool(Name = "dump_palette", ReadOnly = true, Destructive = false)]
    [Description("Decodes all 32 CRAM colors as RGB (SMS RGB222 or Game Gear RGB444).")]
    public static CallToolResult DumpPalette(SmsDebugSession session) => Call(session, session.DumpPalette);

    [McpServerTool(Name = "dump_sprites", ReadOnly = true, Destructive = false)]
    [Description("Decodes the 64 Mode 4 sprite attribute entries: positions, effective pattern bank, end marker and entries after it.")]
    public static CallToolResult DumpSprites(SmsDebugSession session) => Call(session, session.DumpSprites);

    [McpServerTool(Name = "dump_oam", ReadOnly = true, Destructive = false)]
    [Description("Compatibility alias for dump_sprites (SMS sprites reside in VRAM's SAT).")]
    public static CallToolResult DumpOam(SmsDebugSession session) => DumpSprites(session);

    [McpServerTool(Name = "dump_tilemap", ReadOnly = true, Destructive = false)]
    [Description("Decodes the Mode 4 name table: 32 columns, 28/32 rows, tile indices, palette, flip flags and background priority.")]
    public static CallToolResult DumpTilemap(SmsDebugSession session) => Call(session, session.DumpTilemap);

    [McpServerTool(Name = "capture_tilemap", ReadOnly = true, Destructive = false)]
    [Description("Renders the full unscrolled Mode 4 name table as inline PNG, honoring tile flips and palette. Optional new relative .png artifact.")]
    public static CallToolResult CaptureTilemap(SmsDebugSession session, string? path = null)
        => Call(session, () => Png(session.CaptureTilemap(), path));

    [McpServerTool(Name = "dump_tileset", ReadOnly = true, Destructive = false)]
    [Description("Returns Mode 4 planar tiles as inline PNG: start 0..511, count up to remaining tiles, palette 0/1. Optional new relative .png artifact.")]
    public static CallToolResult DumpTileset(SmsDebugSession session, int start = 0, int count = 256, int palette = 0, string? path = null)
        => Call(session, () => Png(session.DumpTileset(start, count, palette), path));

    [McpServerTool(Name = "observe_execution", Destructive = false)]
    [Description("Correlates CPU/VDP/PSG/YM2413, up to 32 CPU memory probes and screen hash every sampleEvery frames for <=600 frames, honoring all traps.")]
    public static CallToolResult ObserveExecution(SmsDebugSession session, int frames = 60, int sampleEvery = 1, int[]? addresses = null, CancellationToken cancellationToken = default)
        => Call(session, () => session.ObserveExecution(frames, sampleEvery, addresses, cancellationToken));

    [McpServerTool(Name = "observe_screen", Destructive = false)]
    [Description("Alias of observe_execution; includes changed-screen hashes correlated with CPU, RAM, VDP, PSG and YM2413.")]
    public static CallToolResult ObserveScreen(SmsDebugSession session, int frames = 60, int sampleEvery = 1, int[]? addresses = null, CancellationToken cancellationToken = default)
        => ObserveExecution(session, frames, sampleEvery, addresses, cancellationToken);

    [McpServerTool(Name = "trace_writes", Destructive = false)]
    [Description("Traces CPU/I/O/VRAM/CRAM/VDP writes for <=600 frames, with writer PC/bank, instruction-start cycle, frame and scanline. Explicit truncation at maxEntries (1..16384).")]
    public static CallToolResult TraceWrites(SmsDebugSession session, string space = "io", int frames = 1, int maxEntries = 4096, CancellationToken cancellationToken = default)
        => Call(session, () => session.TraceWrites(space, frames, maxEntries, cancellationToken));

    [McpServerTool(Name = "trace_video_writes", Destructive = false)]
    [Description("Traces hardware VRAM writes with PC/cycle/frame/scanline correlation; bounded and breakpoint-aware.")]
    public static CallToolResult TraceVideoWrites(SmsDebugSession session, int frames = 1, int maxEntries = 4096, CancellationToken cancellationToken = default)
        => TraceWrites(session, "vram", frames, maxEntries, cancellationToken);

    [McpServerTool(Name = "trace_psg_writes", Destructive = false)]
    [Description("Traces I/O writes during execution; filters PSG ports (0x40..0x7F) and GG stereo port 6. Use trace_writes io for all ports.")]
    public static CallToolResult TracePsgWrites(SmsDebugSession session, int frames = 1, int maxEntries = 4096, CancellationToken cancellationToken = default)
        => Call(session, () => session.TraceWrites("io", frames, maxEntries, cancellationToken, psgOnly: true));

    [McpServerTool(Name = "trace_fm_writes", Destructive = false)]
    [Description("Traces YM2413 port writes (F0 address, F1 data, F2 audio control) with writer PC/bank, cycle, frame and scanline. Requires an SMS loaded with fm=true.")]
    public static CallToolResult TraceFmWrites(SmsDebugSession session, int frames = 1, int maxEntries = 4096, CancellationToken cancellationToken = default)
        => Call(session, () => session.TraceWrites("io", frames, maxEntries, cancellationToken, fmOnly: true));

    [McpServerTool(Name = "find_last_writer", ReadOnly = true, Destructive = false)]
    [Description("Finds the last CPU-originated write to an address in a space since load/reset/restore. Returns null if none was observed; debugger edits are excluded.")]
    public static CallToolResult FindLastWriter(SmsDebugSession session, string address, string space = "cpu")
        => Call(session, () => session.FindLastWriter(Address(address), space));

    [McpServerTool(Name = "read_psg_state", ReadOnly = true, Destructive = false)]
    [Description("Reads three PSG tone periods/frequencies, four attenuation levels, noise control/LFSR and sample rate.")]
    public static CallToolResult ReadPsgState(SmsDebugSession session) => Call(session, session.ReadPsgState);

    [McpServerTool(Name = "read_fm_state", ReadOnly = true, Destructive = false)]
    [Description("Reads the YM2413: port F2 audio control and which chips are audible, address latch, 64 registers, rhythm mode/keys, muted voices and per-channel instrument, attenuation, F-number, block, key/sustain and frequency.")]
    public static CallToolResult ReadFmState(SmsDebugSession session) => Call(session, session.ReadFmState);

    [McpServerTool(Name = "set_audio_channel", Destructive = false)]
    [Description("Enables/mutes a voice in subsequent audio captures. chip psg: tone 1..3 or noise 4. chip fm: melody 1..9 or rhythm bd/sd/tom/cym/hh.")]
    public static CallToolResult SetAudioChannel(SmsDebugSession session, string channel, bool enabled, string chip = "psg")
        => Call(session, () => session.SetAudioChannel(channel, enabled, chip));

    [McpServerTool(Name = "capture_audio", Destructive = false)]
    [Description("Executes <=600 frames and captures the PSG plus YM2413 mix (per port F2) as PCM16 WAV at 44100 Hz, mono SMS/stereo GG, honoring traps. Returns base64 WAV or writes a new relative .wav, plus sample count/RMS/peak and execution result.")]
    public static CallToolResult CaptureAudio(SmsDebugSession session, int frames = 60, string? path = null, CancellationToken cancellationToken = default)
        => Call(session, () =>
        {
            if (path != null) _ = Artifacts.Resolve(path, ".wav");
            var audio = session.CaptureAudio(frames, cancellationToken);
            return new { MimeType = "audio/wav", SampleRate = 44100, SampleValues = audio.Samples.Length,
                audio.Rms, audio.Peak, audio.Result, Artifact = path == null ? null : Artifacts.Write(path, ".wav", audio.Wav),
                WavBase64 = path == null ? Convert.ToBase64String(audio.Wav) : null };
        });

    [McpServerTool(Name = "save_state", ReadOnly = true, Destructive = false)]
    [Description("Captures a version-bound complete emulator snapshot including CPU/VDP/PSG/YM2413 phase, banks, RAM, input and buffers. Returns base64 or writes a new relative .smsstate file.")]
    public static CallToolResult SaveState(SmsDebugSession session, string? path = null)
        => Call(session, () => path == null ? new { StateBase64 = Convert.ToBase64String(session.SaveState()) } : Artifacts.Write(path, ".smsstate", session.SaveState()));

    [McpServerTool(Name = "load_state", Destructive = false)]
    [Description("Restores an emulator snapshot from exactly one of local path or stateBase64. Requires matching ROM hash/mapper/system/FM unit/timing/version; preserves breakpoints, clears writer history.")]
    public static CallToolResult LoadState(SmsDebugSession session, string? path = null, string? stateBase64 = null)
        => Call(session, () =>
        {
            if ((path == null) == (stateBase64 == null)) throw new ArgumentException("Supply exactly one of path and stateBase64.");
            if (path != null && new FileInfo(path).Length > 8_388_608 || stateBase64?.Length > 11_184_812)
                throw new ArgumentException("Snapshot exceeds 8 MiB.");
            return session.LoadState(path != null ? File.ReadAllBytes(path) : Convert.FromBase64String(stateBase64!));
        });

    [McpServerTool(Name = "load_symbols", Destructive = false)]
    [Description("Loads a local symbol file with hex ADDR Name or BANK:ADDR Name lines and ;/# comments. Replaces previous symbols.")]
    public static CallToolResult LoadSymbols(SmsDebugSession session, string path) => Call(session, () => session.LoadSymbols(path));

    [McpServerTool(Name = "resolve_symbol", ReadOnly = true, Destructive = false)]
    [Description("Resolves a loaded symbol's CPU address and optional ROM bank.")]
    public static CallToolResult ResolveSymbol(SmsDebugSession session, string name) => Call(session, () => session.ResolveSymbol(name));

    [McpServerTool(Name = "read_symbol", ReadOnly = true, Destructive = false)]
    [Description("Reads current CPU memory at a loaded symbol, checking its ROM bank is mapped.")]
    public static CallToolResult ReadSymbol(SmsDebugSession session, string name, int length = 1) => Call(session, () => session.ReadSymbol(name, length));
}
