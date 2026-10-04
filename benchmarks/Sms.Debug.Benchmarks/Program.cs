using System.Diagnostics;
using Sms.Debug.Emulator;

// Measures the run loop with nothing observed, with watchpoints that never match, and with the
// frame loop RetroSharp's MasterSystemTestMachine drives. Run in Release:
//   dotnet run -c Release --project benchmarks/Sms.Debug.Benchmarks -- [frames]
var frames = args.Length > 0 ? int.Parse(args[0]) : 300;
var rom = StreamingRom();

Measure("0 watchpoints", frames, s => { for (var f = 0; f < frames; f++) s.RunFrame(); });
Measure("1 VRAM watchpoint, no hit", frames, s =>
{
    s.SetWatchpoint(0x2000, space: "vram");
    for (var f = 0; f < frames; f++) s.RunFrame();
});
Measure("10 watchpoints and 1 breakpoint, no hit", frames, s =>
{
    UnmatchedObservers(s);
    for (var f = 0; f < frames; f++) s.RunFrame();
});
Measure("RunUntilCondition SCANLINE == 27", frames, s =>
{
    for (var f = 0; f < frames; f++)
    {
        s.RunUntilCondition("SCANLINE == 28");
        s.RunUntilCondition("SCANLINE == 27");
    }
});
Measure("RunUntilScanline 27", frames, s =>
{
    for (var f = 0; f < frames; f++)
    {
        s.RunUntilScanline(28);
        s.RunUntilScanline(27);
    }
});
Measure("RetroSharp frame loop", frames, s =>
{
    UnmatchedObservers(s);
    for (var f = 0; f < frames; f++) RetroSharpFrame(s);
});
Measure("RetroSharp frame loop, recording", frames, s =>
{
    UnmatchedObservers(s);
    for (var f = 0; f < frames; f++) RecordingFrame(s);
});

static void Measure(string name, int frames, Action<SmsDebugSession> run)
{
    for (var pass = 0; pass < 2; pass++)
    {
        var session = new SmsDebugSession();
        session.LoadRomBytes(rom(), fm: false);
        // One frame sets up the VDP and the session's last-writer tables, so the figures are steady state.
        session.RunFrame();
        var startInstructions = session.GetState().Instructions;
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        run(session);
        clock.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var instructions = session.GetState().Instructions - startInstructions;
        // The first pass warms up the JIT.
        if (pass == 1)
            Console.WriteLine($"{name,-42} {clock.Elapsed.TotalMilliseconds / frames,8:F3} ms/frame "
                + $"{clock.Elapsed.TotalNanoseconds / instructions,8:F1} ns/instr {(double)allocated / instructions,8:F2} B/instr");
    }

    static byte[] rom() => StreamingRom();
}

// Watchpoints and a breakpoint like the ones a RetroSharp test keeps, at addresses the ROM never touches.
static void UnmatchedObservers(SmsDebugSession s)
{
    s.SetBreakpoint(0x0038);
    s.SetWatchpoint(0x40, 0x40, "io");
    s.SetWatchpoint(0xC800, access: "read");
    s.SetWatchpoint(0xC801);
    s.SetWatchpoint(0xC802);
    s.SetWatchpoint(0xC803);
    s.SetWatchpoint(0xC804);
    s.SetWatchpoint(0, 32, "cram");
    s.SetWatchpoint(0, 11, "vdp");
    s.SetWatchpoint(0x2000, 0x100, "vram");
    s.SetWatchpoint(0xDE00, 0x100);
}

// MasterSystemTestMachine.RunFrame: trace VRAM to the frame boundary, then stop on every VRAM write
// up to the first active line.
static void RetroSharpFrame(SmsDebugSession s)
{
    while (s.TraceWrites("vram", frames: 1, maxEntries: 16384).Result.Reason != "frame_complete") { }
    var watch = s.SetWatchpoint(0, 0x4000, "vram").Id;
    while (s.RunUntilCondition("SCANLINE == 27").Reason != "condition") { }
    s.ClearWatchpoint(watch);
}

// The same frame, recording the VRAM writes up to the first active line in one run instead of one per write.
static void RecordingFrame(SmsDebugSession s)
{
    while (s.TraceWrites("vram", frames: 1, maxEntries: 16384).Result.Reason != "frame_complete") { }
    while (s.TraceWritesUntilScanline(27, "vram", maxEntries: 16384).Result.Reason != "condition") { }
}

// Streams 64 bytes to the name table, then updates 256 RAM bytes, forever; display on, no interrupts.
static byte[] StreamingRom()
{
    var rom = new byte[65536];
    byte[] boot = [0xF3, 0x31, 0xF0, 0xDF, 0xC3, 0x00, 0x01];
    boot.CopyTo(rom, 0);
    var code = new List<byte>();
    void Out(byte value, byte port) => code.AddRange([0x3E, value, 0xD3, port]);
    void Register(byte register, byte value) { Out(value, 0xBF); Out((byte)(0x80 | register), 0xBF); }
    Register(0, 4); Register(1, 0x40); Register(2, 0x0E); Register(5, 0x7E); Register(6, 0);
    var loop = (ushort)(0x100 + code.Count);
    Out(0x00, 0xBF); Out(0x78, 0xBF);
    code.AddRange([0x21, 0x00, 0x00, 0x06, 0x40, 0x0E, 0xBE, 0xED, 0xB3]);
    code.AddRange([0x21, 0x00, 0xC1, 0x06, 0x00, 0x34, 0x2C, 0x10, 0xFC]);
    code.AddRange([0xC3, (byte)loop, (byte)(loop >> 8)]);
    code.ToArray().CopyTo(rom, 0x100);
    return rom;
}
