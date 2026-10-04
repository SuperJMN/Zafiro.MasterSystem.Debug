using System.Text.Json;
using Zafiro.MasterSystem.Debug.Core;
using Zafiro.MasterSystem.Debug.Emulator;
using Zafiro.MasterSystem.Debug.Mcp;

namespace Zafiro.MasterSystem.Debug.Tests;

public sealed class SessionTests
{
    private static SmsDebugSession Session(byte[]? rom = null, string system = "sms", string standard = "ntsc")
    {
        var session = new SmsDebugSession();
        session.LoadRomBytes(rom ?? TestRom.DisplayAndSound(system == "gg"), system: system, standard: standard);
        return session;
    }

    [Fact]
    public void Conditional_breakpoints_stop_before_execution_and_resume_past_the_hit()
    {
        var s = Session(TestRom.Program(0x3E, 1, 0x3C, 0xC3, 2, 0));
        var bp = s.SetBreakpoint(2, "A == 2");
        var stop = s.Continue();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(bp.Id, stop.BreakpointId);
        Assert.Equal(2, s.ReadRegisters().PC);
        Assert.Equal(2, s.ReadRegisters().A);
        var resume = s.Continue(2);
        Assert.Equal(2, resume.InstructionsExecuted);
        Assert.Equal(3, s.ReadRegisters().A);
    }

    [Fact]
    public void Watchpoints_report_the_writer_and_debugger_reads_do_not_trigger_them()
    {
        var s = Session(TestRom.Program(0x3E, 0x42, 0x32, 0, 0xC0, 0xC3, 0, 0));
        var watch = s.SetWatchpoint(0xC000);
        var stop = s.Continue();
        Assert.Equal("watchpoint", stop.Reason);
        Assert.Equal(watch.Id, stop.WatchpointId);
        Assert.Equal(2, stop.Access!.PC);
        Assert.Equal(0x42, s.ReadMemory(0xE000, 1).Bytes[0]);
        Assert.Equal(stop.Access, s.FindLastWriter(0, "ram"));
        s.ClearWatchpoint(watch.Id);
        var read = s.SetWatchpoint(0xC000, access: "read");
        _ = s.ReadMemory(0xC000, 1);
        Assert.Null(s.StepInstruction().WatchpointId);
        s.ClearWatchpoint(read.Id);
    }

    [Fact]
    public void Step_over_and_out_use_the_return_address_and_stack_depth()
    {
        var s = Session(TestRom.Program(0x31, 0xF0, 0xDF, 0xCD, 9, 0, 0x76, 0, 0, 0x3E, 0x55, 0xC9));
        s.StepInstruction();
        Assert.Equal("condition", s.StepOver().Reason);
        Assert.Equal(6, s.ReadRegisters().PC);
        Assert.Equal(0x55, s.ReadRegisters().A);
        s.Reset(); s.StepInstruction(2);
        Assert.Equal(9, s.ReadRegisters().PC);
        Assert.Equal("condition", s.StepOut().Reason);
        Assert.Equal(0xDFF0, s.ReadRegisters().SP);
    }

    [Theory]
    [InlineData("sms", "ntsc", 256, 192)]
    [InlineData("sms", "pal", 256, 192)]
    [InlineData("gg", "ntsc", 160, 144)]
    public void Executed_rom_renders_planar_tiles_with_the_hardware_palette(string system, string standard, int width, int height)
    {
        var s = Session(system: system, standard: standard);
        Assert.Equal("frame_complete", s.RunFrame(2).Reason);
        Assert.Equal(width, s.ReadVdpState().Width);
        Assert.Equal(height, s.ReadVdpState().Height);
        Assert.All(s.ReadScreenRegion(width / 2, height / 2, 8, 8), pixel => Assert.Equal(0xFF0000u, pixel));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, s.CaptureScreen()[..8]);
        Assert.Equal(896, s.DumpTilemap().Length);
        Assert.True(s.DumpSprites()[0].Terminator);
    }

    [Fact]
    public void Vdp_watchpoints_stop_on_cpu_port_writes_and_inspection_preserves_latches()
    {
        var s = Session();
        var wp = s.SetWatchpoint(0, space: "vram");
        var stop = s.Continue();
        Assert.Equal(wp.Id, stop.WatchpointId);
        Assert.Equal(255, stop.Access!.Value);
        s.ClearWatchpoint(wp.Id);
        s.WriteIoPort(0xBF, 0x12);
        var before = s.ReadVdpState();
        _ = s.ReadMemory(0, 64, "vram"); _ = s.CaptureScreen(); _ = s.ReadVdpState();
        Assert.True(s.ReadVdpState().ControlLatchPending);
        Assert.Equal(before.Address, s.ReadVdpState().Address);
    }

    [Theory]
    [InlineData("sms", 1)]
    [InlineData("gg", 2)]
    public void Audio_capture_contains_real_psg_samples_and_channel_mutes(string system, int channels)
    {
        var s = Session(system: system);
        s.RunFrame(2);
        var audio = s.CaptureAudio(3);
        Assert.InRange(audio.Samples.Length / channels, 2207, 2209);
        Assert.True(audio.Peak > 0);
        Assert.True(audio.Samples.Distinct().Count() > 1);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio.Wav[..4]));
        Assert.InRange(s.ReadPsgState().ToneFrequencies[0], 439, 442);
        s.SetAudioChannel("1", false);
        Assert.All(s.CaptureAudio(1).Samples, sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData("sms")]
    [InlineData("gg")]
    public void Subframe_snapshot_replays_identical_memory_video_audio_and_cpu_state(string system)
    {
        var s = Session(system: system);
        s.RunFrame(2); s.StepInstruction(123); s.SetController("right 1");
        var snapshot = s.SaveState();
        var first = s.CaptureAudio(2);
        var memory = s.ReadMemory(0, 8192, "ram").Bytes;
        var screen = s.CaptureScreen();
        var cpu = s.ReadRegisters();
        s.LoadState(snapshot);
        var second = s.CaptureAudio(2);
        Assert.Equal(first.Samples, second.Samples);
        Assert.Equal(memory, s.ReadMemory(0, 8192, "ram").Bytes);
        Assert.Equal(screen, s.CaptureScreen());
        Assert.Equal(cpu, s.ReadRegisters());
    }

    [Fact]
    public void Input_timeline_is_visible_to_the_cpu_and_releases_controls_on_a_trap()
    {
        var s = Session(); s.RunFrame(2);
        s.RunInputTimeline([new(1, "right 1")]);
        Assert.Equal(0xE7, s.ReadMemory(0xC001, 1).Bytes[0]);
        Assert.Equal(255, s.ReadIoPort(0xDC));
        var pc = s.ReadRegisters().PC; s.SetBreakpoint(pc);
        s.RunInputTimeline([new(1, "left")]);
        Assert.Equal(255, s.ReadIoPort(0xDC));
    }

    [Fact]
    public void Game_gear_start_is_a_port_bit_and_sms_pause_is_an_nmi_edge()
    {
        var gg = Session(system: "gg"); gg.SetController("start");
        Assert.Equal(0x40, gg.ReadIoPort(0));
        var rom = TestRom.Program(0x31, 0xF0, 0xDF, 0x76, 0x3E, 0xAB);
        rom[0x66] = 0xED; rom[0x67] = 0x45;
        var sms = Session(rom); sms.StepInstruction(2);
        sms.SetController("pause");
        var nmi = sms.StepInstruction();
        Assert.Equal(11, nmi.CyclesExecuted);
        Assert.Equal(0x66, sms.ReadRegisters().PC);
        sms.StepInstruction(2);
        Assert.Equal(0xAB, sms.ReadRegisters().A);
        Assert.False(sms.ReadRegisters().Halted);
    }

    [Fact]
    public void Sega_banking_preserves_vectors_and_maps_cartridge_ram()
    {
        var rom = TestRom.Program();
        for (var bank = 0; bank < 4; bank++) Array.Fill(rom, (byte)bank, bank * 16384, 16384);
        var s = Session(rom);
        s.WriteMemory(0xFFFD, [3]);
        Assert.Equal(0, s.ReadMemory(0, 1).Bytes[0]);
        Assert.Equal(3, s.ReadMemory(0x400, 1).Bytes[0]);
        s.WriteMemory(0xFFFC, [8]); s.WriteMemory(0x8000, [0x77]);
        Assert.Equal(0x77, s.ReadMemory(0x8000, 1).Bytes[0]);
        s.Reset(); s.WriteMemory(0xFFFC, [8]);
        Assert.Equal(0x77, s.ReadMemory(0x8000, 1).Bytes[0]);
    }

    [Fact]
    public void Bank_qualified_breakpoint_stops_only_in_the_selected_mapping()
    {
        var rom = TestRom.Program(0xC3, 0, 0x40);
        rom[0x4000] = 0xC3; rom[0x4001] = 0; rom[0x4002] = 0x40;
        var s = Session(rom);
        s.SetBreakpoint(0x4000, bank: 2);
        Assert.Equal("instruction_limit", s.Continue(4).Reason);
        s.WriteMemory(0xFFFE, [2]);
        Assert.Equal("breakpoint", s.Continue(4).Reason);
    }

    [Fact]
    public void Trace_truncation_is_explicit_and_observation_reports_actual_frames()
    {
        var s = Session();
        var trace = s.TraceWrites("io", 2, 1);
        Assert.True(trace.Truncated);
        Assert.Single(trace.Writes);
        var observed = s.ObserveExecution(4, 2, [0xC000]);
        Assert.Equal(2, observed.Samples.Length);
        Assert.Equal(4, observed.Result.FramesExecuted);
        Assert.All(observed.Samples, sample => Assert.Single(sample.Memory));
    }

    [Fact]
    public void Cancellation_and_limits_leave_a_paused_inspectable_machine()
    {
        var s = Session(TestRom.Program(0xC3, 0, 0));
        var stop = s.Continue(cancellationToken: new CancellationToken(true));
        Assert.Equal("cancelled", stop.Reason); Assert.Equal(0, stop.InstructionsExecuted);
        Assert.Equal("cycle_limit", s.Continue(maxCycles: 10).Reason);
        Assert.Equal(0, s.ReadRegisters().PC);
    }

    [Fact]
    public void Invalid_mcp_requests_are_protocol_errors_and_do_not_mutate_the_machine()
    {
        var s = Session(); var before = s.ReadRegisters();
        Assert.True(SmsDebugTools.ReadMemory(s, "0xFFFF", 2).IsError);
        Assert.True(SmsDebugTools.WriteMemory(s, "0", "GG").IsError);
        Assert.True(SmsDebugTools.SetBreakpoint(s, "0", "TYPO == 1").IsError);
        Assert.True(SmsDebugTools.RunFrame(s, 1000000).IsError);
        Assert.True(SmsDebugTools.CaptureScreen(s, "../escape.png").IsError);
        Assert.Equal(before, s.ReadRegisters());
    }

    [Fact]
    public void Failed_snapshot_restore_and_rom_load_preserve_the_current_machine()
    {
        var s = Session(); s.RunFrame(1); var before = s.GetState();
        Assert.Throws<JsonException>(() => s.LoadState("garbage"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => s.LoadRomBytes(new byte[10]));
        Assert.Equal(before.Cpu, s.ReadRegisters());
        Assert.Equal(before.Cycles, s.GetState().Cycles);
    }
}
