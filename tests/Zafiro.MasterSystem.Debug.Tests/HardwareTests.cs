using Zafiro.MasterSystem.Debug.Emulator;

namespace Zafiro.MasterSystem.Debug.Tests;

public sealed class HardwareTests
{
    [Theory]
    [InlineData("sega")]
    [InlineData("codemasters")]
    public void Small_rom_banks_mirror_the_available_cartridge_pages(string mapper)
    {
        var rom = new byte[32768];
        Array.Fill(rom, (byte)0x11, 0, 16384); Array.Fill(rom, (byte)0x22, 16384, 16384);
        var s = new SmsDebugSession(); s.LoadRomBytes(rom, mapper: mapper);
        Assert.Equal(0x11, s.ReadMemory(0x8000, 1).Bytes[0]);
        s.WriteMemory(mapper == "sega" ? 0xFFFF : 0x8000, [3]);
        Assert.Equal(0x22, s.ReadMemory(0x8000, 1).Bytes[0]);
    }

    [Fact]
    public void Vblank_interrupt_entry_has_13_cycles_and_does_not_execute_the_vector_opcode()
    {
        var rom = TestRom.Program(0x31, 0xF0, 0xDF, 0xFB, 0x76);
        rom[0x38] = 0x3E; rom[0x39] = 0x55;
        var s = new SmsDebugSession(); s.LoadRomBytes(rom);
        s.WriteMemory(1, [0x60], "vdp");
        s.RunUntilCondition("SCANLINE >= 220");
        // The HALT step sees VDP INT as soon as its line is asserted.
        var result = s.RunUntilCondition("PC == 0x38");
        Assert.Equal("condition", result.Reason);
        Assert.InRange(result.CyclesExecuted, 13, 260);
        Assert.Equal(0, s.ReadRegisters().A);
        Assert.False(s.ReadRegisters().Halted);
        Assert.False(s.ReadRegisters().IFF1);
        Assert.Equal(5, s.ReadMemory(0xDFEE, 1).Bytes[0]);
        var step = s.StepInstruction();
        Assert.Equal(7, step.CyclesExecuted);
        Assert.Equal(0x55, s.ReadRegisters().A);
    }

    [Fact]
    public void Index_and_bit_prefixes_execute_with_their_addressing_modes()
    {
        var s = new SmsDebugSession();
        s.LoadRomBytes(TestRom.Program(0xDD, 0x21, 0, 0xC0, 0xDD, 0x36, 2, 0x80,
            0xDD, 0xCB, 2, 0x06, 0xFD, 0x21, 2, 0xC0, 0xFD, 0x7E, 0, 0xCB, 0x47));
        s.StepInstruction(6);
        Assert.Equal(1, s.ReadMemory(0xC002, 1).Bytes[0]);
        Assert.Equal(1, s.ReadRegisters().A);
        Assert.Equal(0xC000, s.ReadRegisters().IX);
        Assert.Equal(0xC002, s.ReadRegisters().IY);
        Assert.Equal(0, s.ReadRegisters().F & 0x40);
    }

    [Fact]
    public void Codemasters_mapper_switches_rom_and_maps_ram_in_the_top_8_KiB()
    {
        var rom = TestRom.Program();
        for (var bank = 0; bank < 4; bank++) Array.Fill(rom, (byte)bank, bank * 16384, 16384);
        var s = new SmsDebugSession(); s.LoadRomBytes(rom, mapper: "codemasters");
        s.WriteMemory(0, [3]); Assert.Equal(3, s.ReadMemory(0, 1).Bytes[0]);
        s.WriteMemory(0x4000, [0x81]); s.WriteMemory(0xA000, [0xAA]);
        Assert.Equal(2, s.ReadMemory(0x8000, 1).Bytes[0]);
        Assert.Equal(0xAA, s.ReadMemory(0xA000, 1).Bytes[0]);
    }

    [Fact]
    public void Game_gear_stereo_control_routes_tone_to_the_left_channel()
    {
        var s = new SmsDebugSession(); s.LoadRomBytes(TestRom.DisplayAndSound(true), system: "gg");
        s.RunFrame(2); s.WriteIoPort(6, 0x10);
        var samples = s.CaptureAudio(1).Samples;
        Assert.Contains(samples.Where((_, index) => index % 2 == 0), sample => sample != 0);
        Assert.All(samples.Where((_, index) => index % 2 == 1), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Noise_control_resets_the_sega_16_bit_shift_register()
    {
        var s = new SmsDebugSession(); s.LoadRomBytes(TestRom.Program(0xC3, 0, 0));
        s.WriteIoPort(0x7F, 0xE4); s.WriteIoPort(0x7F, 0xF0);
        var samples = s.CaptureAudio(1).Samples;
        Assert.True(samples.Distinct().Count() > 1);
        s.WriteIoPort(0x7F, 0xE4);
        Assert.Equal(0x8000, s.ReadPsgState().NoiseShiftRegister);
    }

    [Fact]
    public void Psg_trace_is_filtered_before_the_entry_limit_is_applied()
    {
        var s = new SmsDebugSession(); s.LoadRomBytes(TestRom.DisplayAndSound());
        var trace = s.TraceWrites("io", 1, 2, psgOnly: true);
        Assert.True(trace.Truncated);
        Assert.Equal(2, trace.Writes.Length);
        Assert.All(trace.Writes, w => Assert.Equal(0x7F, w.Address));
    }
}
