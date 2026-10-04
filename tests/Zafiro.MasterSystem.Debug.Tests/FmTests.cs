using System.Security.Cryptography;
using Emu2413;
using Zafiro.MasterSystem.Debug.Emulator;

namespace Zafiro.MasterSystem.Debug.Tests;

public sealed class FmTests
{
    // Silences the PSG, writes the F2 audio control, stores the F2 read-back at C000 and keys a violin note on FM channel 1.
    private static byte[] FmRom(byte audioControl)
    {
        var code = new List<byte> { 0xF3, 0x31, 0xF0, 0xDF };
        void Out(byte value, byte port) => code.AddRange([0x3E, value, 0xD3, port]);
        void Fm(byte register, byte value) { Out(register, 0xF0); Out(value, 0xF1); }
        foreach (var attenuation in new byte[] { 0x9F, 0xBF, 0xDF, 0xFF }) Out(attenuation, 0x7F);
        Out(audioControl, 0xF2);
        code.AddRange([0xDB, 0xF2, 0x32, 0x00, 0xC0]);
        Fm(0x30, 0x10); Fm(0x10, 0x80); Fm(0x20, 0x15);
        var loop = code.Count;
        code.AddRange([0xC3, (byte)loop, (byte)(loop >> 8)]);
        return TestRom.Program(code.ToArray());
    }

    private static SmsDebugSession Session(byte audioControl, bool fm = true)
    {
        var s = new SmsDebugSession();
        s.LoadRomBytes(FmRom(audioControl), fm: fm);
        s.RunFrame(3);
        return s;
    }

    [Fact]
    public void Fm_note_is_audible_with_the_psg_silenced_and_its_registers_are_inspectable()
    {
        var s = Session(1);
        Assert.Equal(0xF9, s.ReadMemory(0xC000, 1).Bytes[0]);
        var fm = s.ReadFmState();
        Assert.True(fm.FmAudible); Assert.False(fm.PsgAudible);
        var channel = fm.Channels[0];
        Assert.Equal(("violin", 0, 384, 2), (channel.InstrumentName, channel.Attenuation, channel.FNumber, channel.Block));
        Assert.True(channel.KeyOn);
        Assert.InRange(channel.FrequencyHz, 145.5, 145.7);
        var audio = s.CaptureAudio(10);
        Assert.True(audio.Peak > 1000);
        Assert.True(audio.Rms > 0.01);
        s.SetAudioChannel("1", false, "fm");
        Assert.Equal(["1"], s.ReadFmState().MutedChannels);
        Assert.All(s.CaptureAudio(2).Samples, sample => Assert.Equal(0, sample));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Audio_control_port_selects_the_japanese_sms_mixer_output(byte control, bool fmAudible)
    {
        var s = Session(control);
        var audio = s.CaptureAudio(4);
        Assert.Equal(fmAudible, audio.Peak > 0);
        Assert.Equal(fmAudible, s.ReadFmState().FmAudible);
    }

    [Fact]
    public void Export_consoles_and_game_gear_have_no_fm_unit()
    {
        var export = Session(1, fm: false);
        Assert.False(export.GetState().FmUnit);
        Assert.Equal(0xFF, export.ReadMemory(0xC000, 1).Bytes[0]);
        Assert.Throws<InvalidOperationException>(export.ReadFmState);
        Assert.Throws<InvalidOperationException>(() => export.TraceWrites("io", fmOnly: true));
        var gg = new SmsDebugSession();
        Assert.False(gg.LoadRomBytes(TestRom.DisplayAndSound(true), system: "gg").FmUnit);
    }

    [Fact]
    public void Fm_writes_are_traced_with_their_writer()
    {
        var s = new SmsDebugSession();
        s.LoadRomBytes(FmRom(1));
        var trace = s.TraceWrites("io", 1, 100, fmOnly: true);
        Assert.Equal([0xF2, 0xF0, 0xF1, 0xF0, 0xF1, 0xF0, 0xF1], trace.Writes.Select(w => w.Address));
        Assert.Equal(1, trace.Writes[0].Value);
        Assert.All(trace.Writes, w => Assert.InRange(w.PC, 0, 0x100));
    }

    [Fact]
    public void Snapshot_replays_identical_fm_audio_and_rejects_a_different_fm_configuration()
    {
        var s = Session(1);
        s.StepInstruction(77);
        var snapshot = s.SaveState();
        var first = s.CaptureAudio(3);
        var registers = s.ReadFmState().Registers;
        s.LoadState(snapshot);
        Assert.Equal(first.Samples, s.CaptureAudio(3).Samples);
        Assert.Equal(registers, s.ReadFmState().Registers);
        Assert.Throws<ArgumentException>(() => Session(1, fm: false).LoadState(snapshot));
    }

    [Fact]
    public void Port_matches_the_emu2413_reference_output_bit_for_bit()
    {
        // (sample, register, value); the hash was produced by emu2413 v1.5.9 (C) at 3579545 Hz / 44100 Hz.
        int[][] writes =
        [
            [0, 48, 16], [0, 16, 128], [0, 32, 21], [0, 49, 35], [0, 17, 64], [0, 33, 23],
            [2000, 0, 97], [2000, 1, 97], [2000, 2, 30], [2000, 3, 23], [2000, 4, 240], [2000, 5, 127], [2000, 6, 0],
            [2000, 7, 23], [2000, 50, 4], [2000, 18, 200], [2000, 34, 60], [6000, 32, 5],
            [8000, 54, 32], [8000, 55, 81], [8000, 56, 19], [8000, 22, 32], [8000, 23, 80], [8000, 24, 192],
            [8000, 38, 5], [8000, 39, 5], [8000, 40, 1], [8100, 14, 63], [12000, 14, 32], [13000, 14, 53], [16000, 33, 39]
        ];
        var opll = new Opll(3579545, 44100);
        var output = new byte[20000 * 2];
        var next = 0;
        for (var i = 0; i < 20000; i++)
        {
            for (; next < writes.Length && writes[next][0] == i; next++)
            {
                opll.WriteIO(0, (byte)writes[next][1]);
                opll.WriteIO(1, (byte)writes[next][2]);
            }
            BitConverter.TryWriteBytes(output.AsSpan(i * 2), opll.Calc());
        }
        Assert.Equal("b405ee21b939af0b844c27f02b3fe44f39a52dc0ce15eb2351980234a5a0a755", Convert.ToHexStringLower(SHA256.HashData(output)));
    }
}
