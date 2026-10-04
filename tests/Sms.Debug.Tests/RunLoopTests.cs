using Sms.Debug.Core;
using Sms.Debug.Emulator;

namespace Sms.Debug.Tests;

public sealed class RunLoopTests
{
    private static SmsDebugSession Session(byte[] rom, bool fm = true)
    {
        var session = new SmsDebugSession();
        session.LoadRomBytes(rom, fm: fm);
        return session;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_with_unmatched_watchpoints_and_breakpoints_allocates_nothing_per_instruction(bool fm)
    {
        var s = Session(TestRom.StreamingVram(), fm);
        // The first frames set up the VDP and allocate the last-writer tables once per session.
        s.RunFrame(2);
        s.SetBreakpoint(0x38); s.SetBreakpoint(0x66, "A == 1");
        s.SetWatchpoint(0xC800); s.SetWatchpoint(0xC801, access: "read"); s.SetWatchpoint(0x40, 0x40, "io");
        s.SetWatchpoint(0x2000, 0x100, "vram"); s.SetWatchpoint(0, 32, "cram"); s.SetWatchpoint(0, 11, "vdp");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = s.RunFrame(10);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("frame_complete", result.Reason);
        Assert.True(allocated < result.InstructionsExecuted / 10, $"{allocated} bytes for {result.InstructionsExecuted} instructions.");
    }

    [Fact]
    public void Run_until_scanline_stops_where_the_equivalent_condition_does()
    {
        var expected = Session(TestRom.StreamingVram()); var actual = Session(TestRom.StreamingVram());
        foreach (var line in new[] { 100, 27, 0, 261 })
        {
            var condition = expected.RunUntilCondition($"SCANLINE == {line}");
            var scanline = actual.RunUntilScanline(line);
            Assert.Equal("condition", scanline.Reason);
            Assert.Equal(condition with { State = null! }, scanline with { State = null! });
            Assert.Equal(line, actual.ReadVdpState().Scanline);
        }
        Assert.Equal(expected.ReadRegisters(), actual.ReadRegisters());
        Assert.Throws<ArgumentOutOfRangeException>(() => actual.RunUntilScanline(313));
    }

    [Fact]
    public void Recording_writes_up_to_a_scanline_matches_stopping_on_each_watched_write()
    {
        var watched = Session(TestRom.StreamingVram()); var traced = Session(TestRom.StreamingVram());
        watched.RunFrame(); traced.RunFrame();
        var watch = watched.SetWatchpoint(0x3800, 0x40, "vram");
        var writes = new List<BusAccess>(); RunResult run;
        while ((run = watched.RunUntilCondition("SCANLINE == 27")).Reason == "watchpoint") writes.Add(run.Access!);
        watched.ClearWatchpoint(watch.Id);

        var trace = traced.TraceWritesUntilScanline(27, "vram", 0x3800, 0x40);

        Assert.Equal("condition", trace.Result.Reason);
        Assert.False(trace.Truncated);
        Assert.NotEmpty(writes);
        Assert.Equal(writes, trace.Writes);
        Assert.Equal(watched.GetState().Cycles, traced.GetState().Cycles);
        Assert.Equal(watched.ReadRegisters(), traced.ReadRegisters());
        Assert.Equal(writes[^1], traced.FindLastWriter(writes[^1].Address, "vram"));
        Assert.Empty(traced.TraceWritesUntilScanline(27, "vram", 0x2000, 0x100).Writes);
    }

    [Theory]
    [InlineData("HL == 0xC010", 3)]
    [InlineData("[HL] == 7", 5)]
    [InlineData("[0xC010] == 7", 5)]
    [InlineData("B == 9", 7)]
    [InlineData("BC >= 0x0900", 7)]
    public void Compiled_conditions_agree_with_snapshot_evaluation(string expression, int pc)
    {
        // LD HL,$C010; LD (HL),7; LD B,9; JP $0007
        var s = Session(TestRom.Program(0x21, 0x10, 0xC0, 0x36, 7, 0x06, 9, 0xC3, 7, 0));
        Assert.Equal("condition", s.RunUntilCondition(expression).Reason);
        Assert.Equal(pc, s.ReadRegisters().PC);
        Assert.True(Holds(s, expression));
    }

    [Theory]
    [InlineData("FRAME >= 2")]
    [InlineData("VCOUNTER == 100")]
    [InlineData("HCOUNTER >= 0x80")]
    [InlineData("SCANLINE > 192")]
    public void Video_conditions_agree_with_snapshot_evaluation(string expression)
    {
        var s = Session(TestRom.StreamingVram());
        Assert.Equal("condition", s.RunUntilCondition(expression).Reason);
        Assert.True(Holds(s, expression));
    }

    private static bool Holds(SmsDebugSession s, string expression) => Condition.Parse(expression)
        .Evaluate(s.ReadRegisters(), s.ReadVdpState(), s.GetState().Frames, a => s.ReadMemory(a, 1).Bytes[0]);

    [Fact]
    public void Conditional_breakpoints_follow_the_machine_across_reset()
    {
        var s = Session(TestRom.Program(0x3E, 1, 0x3C, 0xC3, 2, 0));
        var bp = s.SetBreakpoint(2, "A == 3");
        Assert.Equal(bp.Id, s.Continue().BreakpointId);
        s.Reset();
        Assert.Equal(bp.Id, s.Continue().BreakpointId);
        Assert.Equal(3, s.ReadRegisters().A);
    }

    [Fact]
    public void Run_time_limit_is_configurable_and_can_be_removed()
    {
        var s = Session(TestRom.Program(0xC3, 0, 0));
        s.RunTimeLimit = TimeSpan.FromTicks(1);
        Assert.Equal("time_limit", s.Continue(5_000_000, 500_000_000).Reason);
        s.RunTimeLimit = null;
        Assert.Equal("instruction_limit", s.Continue(5000).Reason);
    }
}
