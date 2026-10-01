using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Essgee.Emulation.CPU;
using Sms.Debug.Core;

namespace Sms.Debug.Emulator;

public sealed class SmsDebugSession
{
    // The MCP facade serializes every request with this gate, including read-only snapshots.
    public object SyncRoot { get; } = new();
    private SmsMachine? machine;
    private string? romPath, romHash;
    private int nextId = 1;
    private int? lastBreakpoint;
    private readonly Dictionary<int, Breakpoint> breakpoints = [];
    private readonly Dictionary<int, Condition> conditions = [];
    private readonly Dictionary<int, Watchpoint> watchpoints = [];
    private readonly Dictionary<(string, int), BusAccess> lastWriters = [];
    private readonly Dictionary<string, SymbolInfo> symbols = new(StringComparer.Ordinal);
    private SmsMachine Machine => machine ?? throw new InvalidOperationException("Load a ROM first.");

    public SessionState GetState() => new(machine != null, romPath, romHash, machine?.Mapper ?? "sega",
        machine?.GameGear == true ? "gg" : "sms", machine == null ? [] : [machine.BankAt(0x400), machine.BankAt(0x4000), machine.BankAt(0x8000)],
        machine?.BankAt(0xA000) == -1,
        machine?.Standard ?? "ntsc", machine?.FmPresent == true, machine?.Cycles ?? 0, machine?.Instructions ?? 0, machine?.Frames ?? 0,
        machine?.Cpu.InspectRegisters(), machine?.Vdp.InspectState());

    public SessionState LoadRom(string path, string mapper = "sega", string standard = "ntsc", string system = "auto", bool fm = true)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("ROM not found.", full);
        var extension = Path.GetExtension(full).ToLowerInvariant();
        byte[] bytes;
        if (extension == ".zip")
        {
            using var archive = ZipFile.OpenRead(full);
            var entries = archive.Entries.Where(e => Path.GetExtension(e.Name).ToLowerInvariant() is ".sms" or ".gg").ToArray();
            if (entries.Length != 1) throw new ArgumentException("ZIP must contain exactly one .sms or .gg ROM.");
            if (entries[0].Length is < 1024 or > 4194816) throw new ArgumentException("ROM size must be between 1 KiB and 4 MiB (plus optional header).");
            extension = Path.GetExtension(entries[0].Name).ToLowerInvariant();
            using var input = entries[0].Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            bytes = output.ToArray();
        }
        else
        {
            if (new FileInfo(full).Length is < 1024 or > 4194816) throw new ArgumentException("ROM size must be between 1 KiB and 4 MiB (plus optional header).");
            bytes = File.ReadAllBytes(full);
        }
        if (bytes.Length % 16384 == 512) bytes = bytes[512..];
        return LoadRomBytes(bytes, mapper, standard, system == "auto" ? (extension == ".gg" ? "gg" : "sms") : system, full, fm);
    }

    public SessionState LoadRomBytes(byte[] bytes, string mapper = "sega", string standard = "ntsc", string system = "sms", string path = "<memory>", bool fm = true)
    {
        if (bytes.Length is < 1024 or > 4194304) throw new ArgumentException("ROM size must be 1 KiB..4 MiB.");
        if (mapper is not ("sega" or "codemasters")) throw new ArgumentException("Supported mappers: sega, codemasters.");
        if (standard is not ("ntsc" or "pal")) throw new ArgumentException("Standard must be ntsc or pal.");
        if (system is not ("sms" or "gg")) throw new ArgumentException("System must be sms or gg.");
        if (system == "gg" && standard != "ntsc") throw new ArgumentException("Game Gear uses NTSC timing.");
        // The YM2413 exists only on the Japanese SMS / Mark III FM unit; Game Gear never has it.
        var fresh = new SmsMachine((byte[])bytes.Clone(), mapper, standard, system == "gg", fm && system == "sms");
        machine = fresh;
        romPath = path;
        romHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        breakpoints.Clear(); conditions.Clear(); watchpoints.Clear(); lastWriters.Clear(); symbols.Clear();
        nextId = 1; lastBreakpoint = null;
        return GetState();
    }

    public SessionState Reset()
    {
        var m = Machine;
        var fresh = new SmsMachine(m.Rom, m.Mapper, m.Standard, m.GameGear, m.FmPresent);
        fresh.Cartridge.LoadRam((byte[])m.Cartridge.GetRamData().Clone());
        machine = fresh;
        lastWriters.Clear(); lastBreakpoint = null;
        return GetState();
    }

    public CpuRegisters ReadRegisters() => Machine.Cpu.InspectRegisters();
    public VdpState ReadVdpState() => Machine.Vdp.InspectState();
    public PsgState ReadPsgState() => Machine.Psg.InspectState();
    public FmState ReadFmState() => TryReadFmState() ?? throw new InvalidOperationException("No YM2413: Game Gear or ROM loaded with fm=false.");
    private FmState? TryReadFmState() => Machine.Fm?.InspectState(Machine.FmControl, Machine.FmAudible, Machine.PsgAudible);

    private int SpaceSize(string space) => space switch
    {
        "cpu" => 65536, "ram" => 8192, "vram" => 16384, "cram" => Machine.Vdp.DebugCram.Length,
        "vdp" => 11, "rom" => Machine.Rom.Length, "cartram" => 32768, "io" => 256,
        _ => throw new ArgumentException("Space must be cpu, ram, vram, cram, vdp, rom, cartram or io.")
    };

    private void ValidateRange(string space, int address, int length)
    {
        _ = Machine;
        if (length < 1 || length > 65536 || address < 0 || (long)address + length > SpaceSize(space))
            throw new ArgumentOutOfRangeException(nameof(address), "Requested range is outside the memory space or exceeds 65536 bytes.");
    }

    public MemoryBlock ReadMemory(int address, int length = 64, string space = "cpu")
    {
        ValidateRange(space, address, length);
        if (space == "io") throw new ArgumentException("Use read_io_port for side-effectful I/O reads.");
        var m = Machine;
        var bytes = Enumerable.Range(address, length).Select(a => space switch
        {
            "cpu" => m.PeekCpu((ushort)a), "ram" => m.Ram[a], "vram" => m.Vdp.DebugVram[a],
            "cram" => m.Vdp.DebugCram[a], "vdp" => m.Vdp.DebugRegisters[a], "rom" => m.Rom[a],
            "cartram" => m.Cartridge.GetRamData()[a], _ => (byte)0
        }).ToArray();
        return new(space, address, bytes, Convert.ToHexString(bytes));
    }

    public MemoryBlock WriteMemory(int address, byte[] bytes, string space = "cpu")
    {
        ValidateRange(space, address, bytes.Length);
        if (space is "rom" or "io") throw new ArgumentException("ROM is read-only; use write_io_port for I/O.");
        for (var n = 0; n < bytes.Length; n++)
        {
            var a = address + n;
            switch (space)
            {
                case "cpu": Machine.PokeCpu((ushort)a, bytes[n]); break;
                case "ram": Machine.Ram[a] = bytes[n]; break;
                case "vram": Machine.Vdp.DebugVram[a] = bytes[n]; break;
                case "cram": Machine.Vdp.DebugCram[a] = bytes[n]; break;
                case "vdp": Machine.Vdp.DebugWriteRegister(a, bytes[n]); break;
                case "cartram": Machine.Cartridge.GetRamData()[a] = bytes[n]; break;
            }
        }
        return ReadMemory(address, bytes.Length, space);
    }

    public byte ReadIoPort(int port) { ValidateRange("io", port, 1); return Machine.ReadPort((byte)port); }
    public void WriteIoPort(int port, int value)
    {
        ValidateRange("io", port, 1);
        if (value is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(value));
        Machine.WritePort((byte)port, (byte)value);
    }

    public Breakpoint SetBreakpoint(int address, string? condition = null, int? bank = null)
    {
        ValidateRange("cpu", address, 1);
        if (bank is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(bank));
        var parsed = condition == null ? null : Condition.Parse(condition);
        var bp = new Breakpoint(nextId++, (ushort)address, condition, bank);
        breakpoints.Add(bp.Id, bp);
        if (parsed != null) conditions.Add(bp.Id, parsed);
        return bp;
    }

    public bool ClearBreakpoint(int id) { conditions.Remove(id); return breakpoints.Remove(id); }
    public Breakpoint[] ListBreakpoints() => breakpoints.Values.ToArray();
    public Watchpoint SetWatchpoint(int address, int length = 1, string space = "cpu", string access = "write")
    {
        ValidateRange(space, address, length);
        if (space is not ("cpu" or "io" or "vram" or "cram" or "vdp")) throw new ArgumentException("Watchpoints support cpu, io, vram, cram and vdp.");
        if (access is not ("read" or "write" or "readwrite")) throw new ArgumentException("Access must be read, write or readwrite.");
        if (space is "vram" or "cram" or "vdp" && access != "write") throw new ArgumentException("Direct VDP spaces support write watchpoints; watch I/O ports for hardware reads.");
        var wp = new Watchpoint(nextId++, space, address, address + length - 1, access);
        watchpoints.Add(wp.Id, wp);
        return wp;
    }
    public bool ClearWatchpoint(int id) => watchpoints.Remove(id);
    public Watchpoint[] ListWatchpoints() => watchpoints.Values.ToArray();

    public static void Bound(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new ArgumentOutOfRangeException(name, $"{name} must be {min}..{max}.");
    }

    private bool Evaluate(Condition condition) => condition.Evaluate(ReadRegisters(), ReadVdpState(), Machine.Frames, Machine.PeekCpu);

    private RunResult Run(int maxInstructions, long maxCycles, long? targetFrame = null, Condition? condition = null,
        Func<bool>? predicate = null, bool ignoreBreakpoints = false, Action<BusAccess>? trace = null,
        CancellationToken cancellationToken = default)
    {
        Bound(maxInstructions, 1, 5_000_000, nameof(maxInstructions));
        if (maxCycles is < 1 or > 500_000_000) throw new ArgumentOutOfRangeException(nameof(maxCycles));
        var m = Machine;
        var startInstructions = m.Instructions; var startCycles = m.Cycles; var startFrames = m.Frames;
        var deadline = Stopwatch.StartNew();
        BusAccess? hitAccess = null; int? hitWatch = null; int? hitBreak = null;
        var reason = "instruction_limit";
        var skipBreakpoint = lastBreakpoint;
        lastBreakpoint = null;
        m.AccessObserver = a =>
        {
            if (a.Access == "write")
            {
                lastWriters[(a.Space, a.Address)] = a;
                // CPU RAM mirrors identify the same physical write.
                if (a.Space == "cpu" && a.Address >= 0xC000)
                    lastWriters[("ram", a.Address & 0x1FFF)] = a;
            }
            trace?.Invoke(a);
            if (hitWatch == null)
            foreach (var wp in watchpoints.Values)
                if (wp.Space == a.Space && a.Address >= wp.Start && a.Address <= wp.End
                    && (wp.Access == a.Access || wp.Access == "readwrite"))
                { hitWatch = wp.Id; hitAccess = a; break; }
        };
        try
        {
            for (var step = 0; step < maxInstructions; step++)
            {
                if ((step & 1023) == 0)
                {
                    if (cancellationToken.IsCancellationRequested) { reason = "cancelled"; break; }
                    if (deadline.Elapsed.TotalSeconds > 15) { reason = "time_limit"; break; }
                }
                if (targetFrame.HasValue && m.Frames >= targetFrame) { reason = "frame_complete"; break; }
                if (condition != null && Evaluate(condition) || predicate?.Invoke() == true) { reason = "condition"; break; }
                if (m.Cycles - startCycles >= maxCycles) { reason = "cycle_limit"; break; }
                var pc = ReadRegisters().PC;
                if (!ignoreBreakpoints)
                foreach (var bp in breakpoints.Values)
                    if (!(step == 0 && bp.Id == skipBreakpoint) && pc == bp.Address
                        && (!bp.Bank.HasValue || m.BankAt(pc) == bp.Bank)
                        && (!conditions.TryGetValue(bp.Id, out var c) || Evaluate(c)))
                    { hitBreak = bp.Id; break; }
                if (hitBreak.HasValue) { reason = "breakpoint"; lastBreakpoint = hitBreak; break; }
                m.Step();
                if (hitWatch.HasValue) { reason = "watchpoint"; break; }
            }
            if (reason == "instruction_limit" && targetFrame.HasValue && m.Frames >= targetFrame) reason = "frame_complete";
            if (reason == "instruction_limit" && (condition != null && Evaluate(condition) || predicate?.Invoke() == true)) reason = "condition";
        }
        finally { m.AccessObserver = null; }
        return new(reason, m.Instructions - startInstructions, m.Cycles - startCycles, m.Frames - startFrames,
            hitBreak, hitWatch, hitAccess, GetState());
    }

    public RunResult StepInstruction(int count = 1, CancellationToken cancellationToken = default)
    { Bound(count, 1, 10000, nameof(count)); return Run(count, 500_000_000, ignoreBreakpoints: true, cancellationToken: cancellationToken); }
    public RunResult Continue(int maxInstructions = 1_000_000, long maxCycles = 20_000_000, CancellationToken cancellationToken = default)
        => Run(maxInstructions, maxCycles, cancellationToken: cancellationToken);
    public RunResult RunUntilCondition(string expression, int maxInstructions = 1_000_000, CancellationToken cancellationToken = default)
        => Run(maxInstructions, 20_000_000, condition: Condition.Parse(expression), cancellationToken: cancellationToken);
    public RunResult RunFrame(int frames = 1, CancellationToken cancellationToken = default)
    { Bound(frames, 1, 600, nameof(frames)); return Run(5_000_000, 500_000_000, Machine.Frames + frames, cancellationToken: cancellationToken); }

    public RunResult StepOver(int maxInstructions = 1_000_000, CancellationToken cancellationToken = default)
    {
        Bound(maxInstructions, 1, 5_000_000, nameof(maxInstructions));
        var cpu = ReadRegisters();
        var bytes = OpcodeBytes(cpu.PC);
        var op = bytes[0];
        if (op != 0xCD && (op & 0xC7) != 0xC4 && (op & 0xC7) != 0xC7) return StepInstruction(cancellationToken: cancellationToken);
        var target = (ushort)(cpu.PC + Z80A.DisassembleGetOpcodeLen(Machine.Cpu, bytes));
        var first = StepInstruction(cancellationToken: cancellationToken);
        if (first.WatchpointId != null || cancellationToken.IsCancellationRequested) return first;
        var result = Run(maxInstructions, 20_000_000, predicate: () => ReadRegisters().PC == target && ReadRegisters().SP == cpu.SP,
            cancellationToken: cancellationToken);
        return result with { InstructionsExecuted = result.InstructionsExecuted + first.InstructionsExecuted,
            CyclesExecuted = result.CyclesExecuted + first.CyclesExecuted, FramesExecuted = result.FramesExecuted + first.FramesExecuted };
    }

    public RunResult StepOut(int maxInstructions = 1_000_000, CancellationToken cancellationToken = default)
    {
        var sp = ReadRegisters().SP;
        var target = Machine.PeekCpu(sp) | Machine.PeekCpu((ushort)(sp + 1)) << 8;
        return Run(maxInstructions, 20_000_000, predicate: () => ReadRegisters().PC == target && ReadRegisters().SP == (ushort)(sp + 2),
            cancellationToken: cancellationToken);
    }

    private byte[] OpcodeBytes(ushort pc) => Enumerable.Range(0, 5).Select(n => Machine.PeekCpu((ushort)(pc + n))).ToArray();
    public DisassembledInstruction[] Disassemble(int address, int count = 16)
    {
        ValidateRange("cpu", address, 1); Bound(count, 1, 256, nameof(count));
        var result = new List<DisassembledInstruction>(); var pc = (ushort)address;
        for (var n = 0; n < count; n++)
        {
            var bytes = OpcodeBytes(pc); var length = Math.Clamp(Z80A.DisassembleGetOpcodeLen(Machine.Cpu, bytes), 1, 5);
            result.Add(new(pc, Machine.BankAt(pc), bytes[..length], Z80A.DisassembleMakeMnemonicString(Machine.Cpu, bytes)));
            pc += (ushort)length;
        }
        return result.ToArray();
    }

    public void SetController(string buttons, int player = 1)
    {
        Bound(player, 1, 2, nameof(player));
        byte mask = 0; bool pause = false, reset = false;
        foreach (var token in buttons.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            switch (token.ToLowerInvariant())
            {
                case "up": mask |= 1; break; case "down": mask |= 2; break; case "left": mask |= 4; break;
                case "right": mask |= 8; break; case "1": case "b1": case "a": mask |= 16; break;
                case "2": case "b2": case "b": mask |= 32; break; case "pause": case "start": pause = true; break;
                case "reset": reset = true; break; default: throw new ArgumentException($"Unknown button: {token}.");
            }
        Machine.SetController(player, mask, pause, reset);
    }

    public RunResult[] RunInputTimeline(InputSegment[] segments, bool release = true, CancellationToken cancellationToken = default)
    {
        if (segments.Length is < 1 or > 120 || segments.Any(s => s.Frames < 1 || s.Frames > 600 || s.Player is < 1 or > 2)
            || segments.Sum(s => (long)s.Frames) > 600) throw new ArgumentException("Timeline must have 1..120 segments totaling at most 600 frames.");
        // Validate all buttons before starting any execution.
        foreach (var s in segments) ValidateButtons(s.Buttons);
        var results = new List<RunResult>();
        try
        {
            foreach (var s in segments)
            {
                SetController(s.Buttons, s.Player);
                var result = RunFrame(s.Frames, cancellationToken); results.Add(result);
                if (result.Reason != "frame_complete") break;
            }
        }
        finally { if (release) { SetController("", 1); SetController("", 2); } }
        return results.ToArray();
    }

    private static void ValidateButtons(string buttons)
    {
        var valid = new HashSet<string> { "up", "down", "left", "right", "1", "b1", "a", "2", "b2", "b", "pause", "start", "reset" };
        if (buttons.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => !valid.Contains(t.ToLowerInvariant()))) throw new ArgumentException("Unknown timeline button.");
    }

    public uint[] ScreenPixels() => Machine.Vdp.InspectPixels();
    public byte[] CaptureScreen() { var s = ReadVdpState(); return PngEncoder.EncodeRgb24(ScreenPixels(), s.Width, s.Height); }

    public uint[] ReadScreenRegion(int x, int y, int width, int height)
    {
        var s = ReadVdpState();
        if (x < 0 || y < 0 || width < 1 || height < 1 || (long)x + width > s.Width || (long)y + height > s.Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Rectangle must lie within the active screen.");
        var pixels = ScreenPixels();
        return Enumerable.Range(y, height).SelectMany(row => pixels.AsSpan(row * s.Width + x, width).ToArray()).ToArray();
    }

    public object[] DumpPalette() => Enumerable.Range(0, 32).Select(i => (object)new { Index = i, Rgb = $"#{Machine.Vdp.DebugColor(i):X6}" }).ToArray();
    private void RequireMode4() { if (!ReadVdpState().Mode4) throw new NotSupportedException("Tile/sprite decoding currently supports VDP Mode 4."); }

    public SpriteInfo[] DumpSprites()
    {
        RequireMode4(); var s = ReadVdpState(); var v = Machine.Vdp.DebugVram; var result = new List<SpriteInfo>(); bool terminated = false;
        for (var i = 0; i < 64; i++)
        {
            var y = v[(s.SpriteTableAddress + i) & 16383];
            var end = y == 0xD0 && Machine.Vdp.ScreenHeight == 192;
            var x = v[(s.SpriteTableAddress + 128 + i * 2) & 16383] - ((s.Registers[0] & 8) != 0 ? 8 : 0);
            var pattern = v[(s.SpriteTableAddress + 129 + i * 2) & 16383];
            var patternIndex = pattern | ((s.SpritePatternAddress >> 5) & 256);
            if ((s.Registers[1] & 2) != 0) patternIndex &= ~1;
            result.Add(new(i, x, y >= 0xE0 ? y - 255 : y + 1, patternIndex, end, terminated));
            terminated |= end;
        }
        return result.ToArray();
    }

    public TileInfo[] DumpTilemap()
    {
        RequireMode4(); var s = ReadVdpState(); var v = Machine.Vdp.DebugVram;
        return Enumerable.Range(0, 32 * (s.NametableHeight / 8)).Select(n =>
        {
            var a = (s.NameTableAddress + n * 2) & 16383; var word = (ushort)(v[a] | v[(a + 1) & 16383] << 8);
            return new TileInfo(n, word & 511, (word & 512) != 0, (word & 1024) != 0, (word >> 11) & 1, (word & 4096) != 0, word);
        }).ToArray();
    }

    private int TilePixel(int tile, int x, int y)
    {
        var a = tile * 32 + y * 4; var v = Machine.Vdp.DebugVram;
        return Enumerable.Range(0, 4).Sum(plane => ((v[(a + plane) & 16383] >> (7 - x)) & 1) << plane);
    }

    public byte[] DumpTileset(int start = 0, int count = 256, int palette = 0)
    {
        RequireMode4(); Bound(start, 0, 511, nameof(start)); Bound(count, 1, 512 - start, nameof(count)); Bound(palette, 0, 1, nameof(palette));
        var columns = Math.Min(16, count); var rows = (count + columns - 1) / columns;
        var pixels = new uint[columns * 8 * rows * 8];
        for (var n = 0; n < count; n++) for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
            pixels[(n / columns * 8 + y) * columns * 8 + n % columns * 8 + x] = Machine.Vdp.DebugColor(palette * 16 + TilePixel(start + n, x, y));
        return PngEncoder.EncodeRgb24(pixels, columns * 8, rows * 8);
    }

    public byte[] CaptureTilemap()
    {
        var tiles = DumpTilemap(); var height = ReadVdpState().NametableHeight; var pixels = new uint[256 * height];
        foreach (var tile in tiles) for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
            pixels[(tile.Index / 32 * 8 + y) * 256 + tile.Index % 32 * 8 + x] = Machine.Vdp.DebugColor(tile.Palette * 16
                + TilePixel(tile.Tile, tile.FlipX ? 7 - x : x, tile.FlipY ? 7 - y : y));
        return PngEncoder.EncodeRgb24(pixels, 256, height);
    }

    public ObservedExecution ObserveExecution(int frames = 60, int sampleEvery = 1, int[]? addresses = null,
        CancellationToken cancellationToken = default)
    {
        Bound(frames, 1, 600, nameof(frames)); Bound(sampleEvery, 1, frames, nameof(sampleEvery));
        addresses ??= [];
        if (addresses.Length > 32) throw new ArgumentException("At most 32 RAM probe addresses.");
        foreach (var a in addresses) ValidateRange("cpu", a, 1);
        var samples = new List<Observation>(); var start = GetState(); RunResult result = null!;
        for (var done = 0; done < frames;)
        {
            var interval = Math.Min(sampleEvery, frames - done); result = RunFrame(interval, cancellationToken); done += interval;
            samples.Add(new(Machine.Frames, ReadRegisters(), ReadVdpState(), ReadPsgState(), TryReadFmState(), addresses.Select(a => ReadMemory(a, 1)).ToArray(),
                Convert.ToHexStringLower(SHA256.HashData(CaptureScreen()))));
            if (result.Reason != "frame_complete") break;
        }
        return new(samples.ToArray(), result with { InstructionsExecuted = Machine.Instructions - start.Instructions,
            CyclesExecuted = Machine.Cycles - start.Cycles, FramesExecuted = Machine.Frames - start.Frames });
    }

    public WriteTrace TraceWrites(string space = "io", int frames = 1, int maxEntries = 4096, CancellationToken cancellationToken = default,
        bool psgOnly = false, bool fmOnly = false)
    {
        if (fmOnly && !Machine.FmPresent) throw new InvalidOperationException("No YM2413: Game Gear or ROM loaded with fm=false.");
        _ = SpaceSize(space); Bound(frames, 1, 600, nameof(frames)); Bound(maxEntries, 1, 16384, nameof(maxEntries));
        var entries = new List<BusAccess>(); bool truncated = false;
        var result = Run(5_000_000, 500_000_000, Machine.Frames + frames, trace: a =>
        {
            if (a.Access != "write" || a.Space != space || (psgOnly && (a.Address & 0xC0) != 0x40 && !(Machine.GameGear && a.Address == 6))
                || (fmOnly && a.Address is not (0xF0 or 0xF1 or 0xF2))) return;
            if (entries.Count < maxEntries) entries.Add(a); else truncated = true;
        }, cancellationToken: cancellationToken);
        return new(entries.ToArray(), truncated, result);
    }

    public BusAccess? FindLastWriter(int address, string space = "cpu")
    { ValidateRange(space, address, 1); return lastWriters.GetValueOrDefault((space, address)); }

    public AudioCapture CaptureAudio(int frames = 60, CancellationToken cancellationToken = default)
    {
        Bound(frames, 1, 600, nameof(frames)); var samples = new List<short>();
        Machine.SampleObserver = block => samples.AddRange(block);
        RunResult result;
        try { result = RunFrame(frames, cancellationToken); }
        finally { Machine.SampleObserver = null; }
        var array = samples.ToArray();
        var rms = array.Length == 0 ? 0 : Math.Sqrt(array.Average(s => (double)s * s)) / 32768;
        var peak = array.Length == 0 ? 0 : array.Max(s => Math.Abs((int)s));
        return new(WavEncoder.Encode(array, SmsMachine.SampleRate, Machine.Channels), array, rms, peak, result);
    }

    public object SetAudioChannel(string channel, bool enabled, string chip = "psg")
    {
        var m = Machine;
        if (chip == "psg")
        {
            var number = int.TryParse(channel, out var n) ? n : 0;
            Bound(number, 1, 4, nameof(channel));
            m.Psg.SetRuntimeOption($"AudioEnableCh{number}{(number == 4 ? "Noise" : "Square")}", enabled);
            return new { Chip = chip, Channel = channel, Enabled = enabled };
        }
        if (chip != "fm") throw new ArgumentException("Chip must be psg or fm.");
        var fm = m.Fm ?? throw new InvalidOperationException("No YM2413: Game Gear or ROM loaded with fm=false.");
        var rhythm = Array.IndexOf(Emu2413.Opll.RhythmNames, channel.ToLowerInvariant());
        var bit = rhythm >= 0 ? 9 + rhythm : int.TryParse(channel, out var c) && c is >= 1 and <= 9 ? c - 1
            : throw new ArgumentException("FM channel must be 1..9 or a rhythm voice: bd, sd, tom, cym, hh.");
        fm.SetMask(enabled ? fm.DebugMask & ~(1u << bit) : fm.DebugMask | 1u << bit);
        return new { Chip = chip, Channel = channel, Enabled = enabled };
    }

    private sealed record Snapshot(int Version, string RomHash, string Mapper, string Standard, bool GameGear, bool Fm,
        Dictionary<string, Dictionary<string, JsonElement>> Data);

    public byte[] SaveState()
    {
        var m = Machine;
        return JsonSerializer.SerializeToUtf8Bytes(new Snapshot(2, romHash!, m.Mapper, m.Standard, m.GameGear, m.FmPresent, MachineSnapshot.Capture(m)));
    }

    public SessionState LoadState(byte[] bytes)
    {
        var m = Machine;
        var state = JsonSerializer.Deserialize<Snapshot>(bytes) ?? throw new ArgumentException("Invalid snapshot.");
        if (state.Version != 2 || state.RomHash != romHash || state.Mapper != m.Mapper || state.Standard != m.Standard
            || state.GameGear != m.GameGear || state.Fm != m.FmPresent)
            throw new ArgumentException("Snapshot version, ROM hash, mapper, system, FM unit or timing does not match the loaded ROM.");
        var fresh = new SmsMachine(m.Rom, m.Mapper, m.Standard, m.GameGear, m.FmPresent);
        MachineSnapshot.Restore(fresh, state.Data);
        machine = fresh;
        lastWriters.Clear(); lastBreakpoint = null;
        return GetState();
    }

    public SymbolInfo[] LoadSymbols(string path)
    {
        if (new FileInfo(path).Length > 4_194_304) throw new ArgumentException("Symbol file exceeds 4 MiB.");
        var parsed = new Dictionary<string, SymbolInfo>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var text = line.Split(';', '#')[0].Trim(); if (text.Length == 0) continue;
            var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2) throw new ArgumentException($"Invalid symbol line: {text}.");
            var parts = fields[0].Split(':');
            if (parts.Length is < 1 or > 2) throw new ArgumentException("Symbol address must be ADDR or BANK:ADDR in hex.");
            var address = Convert.ToUInt16(parts[^1], 16);
            var bank = parts.Length == 2 ? Convert.ToInt32(parts[0], 16) : (int?)null;
            parsed.Add(fields[1], new(fields[1], address, bank));
        }
        symbols.Clear(); foreach (var pair in parsed) symbols.Add(pair.Key, pair.Value);
        return symbols.Values.ToArray();
    }

    public SymbolInfo ResolveSymbol(string name) => symbols.TryGetValue(name, out var symbol) ? symbol : throw new ArgumentException($"Symbol not found: {name}.");
    public MemoryBlock ReadSymbol(string name, int length = 1)
    {
        var symbol = ResolveSymbol(name);
        if (symbol.Bank.HasValue && Machine.BankAt(symbol.Address) != symbol.Bank) throw new InvalidOperationException("Symbol bank is not currently mapped.");
        return ReadMemory(symbol.Address, length);
    }
}
