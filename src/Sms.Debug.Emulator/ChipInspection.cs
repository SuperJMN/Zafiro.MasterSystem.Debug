using Essgee.Emulation;
using Sms.Debug.Core;

namespace Essgee.Emulation.CPU
{
    public partial class Z80A
    {
        public CpuRegisters InspectRegisters() => new(af.Word, bc.Word, de.Word, hl.Word, ix.Word, iy.Word,
            sp, pc, af_.Word, bc_.Word, de_.Word, hl_.Word, i, r, iff1, iff2, im, halt);
    }
}

namespace Essgee.Emulation.Video
{
    public partial class SegaSMSVDP
    {
        public byte[] DebugVram => vram;
        public byte[] DebugCram => cram;
        public byte[] DebugRegisters => registers;
        public VdpState InspectState() => new((byte[])registers.Clone(), (byte)statusFlags, currentScanline,
            vCounter, hCounter, this is SegaGGVDP ? 160 : 256, this is SegaGGVDP ? 144 : screenHeight,
            nametableHeight, nametableBaseAddress, spriteAttribTableBaseAddress, spritePatternGenBaseAddress,
            addressRegister, codeRegister, isSecondControlWrite, InterruptLine == InterruptState.Assert,
            isBitM4Set, !isDisplayBlanked, isFrameInterruptPending, isLineInterruptPending);

        // These are direct debugger accesses, without changing the VDP port latch/read buffer.
        public void DebugWriteRegister(int index, byte value) => WriteRegister((byte)index, value);
        public uint[] InspectPixels()
        {
            var state = InspectState();
            var pixels = new uint[state.Width * state.Height];
            for (var y = 0; y < state.Height; y++)
            for (var x = 0; x < state.Width; x++)
            {
                var offset = this is SegaGGVDP ? (y * 160 + x) * 4
                    : ((y + scanlineActiveDisplay) * numVisiblePixels + x + pixelActiveDisplay) * 4;
                if (offset + 2 < outputFramebuffer.Length)
                    pixels[y * state.Width + x] = (uint)(outputFramebuffer[offset + 2] << 16
                        | outputFramebuffer[offset + 1] << 8 | outputFramebuffer[offset]);
            }
            return pixels;
        }

        public uint DebugColor(int index)
        {
            var gg = this is SegaGGVDP;
            var c = gg ? cram[index * 2] | cram[index * 2 + 1] << 8 : cram[index];
            var mask = gg ? 15 : 3;
            var shift = gg ? 4 : 2;
            var scale = gg ? 17 : 85;
            return (uint)(((c & mask) * scale << 16) | (((c >> shift) & mask) * scale << 8)
                | ((c >> (shift * 2)) & mask) * scale);
        }
    }
}

namespace Essgee.Emulation.Audio
{
    public partial class SN76489
    {
        public Action<short[]>? SampleObserver;
        public PsgState InspectState() => new(toneRegisters.Take(3).ToArray(), (ushort[])volumeRegisters.Clone(),
            toneRegisters[3], noiseLfsr, toneRegisters.Take(3)
                .Select(t => t < 2 ? 0 : clockRate / (32 * t)).ToArray(), sampleRate);
    }
}

namespace Emu2413
{
    public sealed partial class Opll
    {
        private static readonly string[] InstrumentNames = ["user", "violin", "guitar", "piano", "flute", "clarinet",
            "oboe", "trumpet", "organ", "horn", "synthesizer", "harpsichord", "vibraphone", "synth bass",
            "acoustic bass", "electric guitar"];
        // Mask bit order follows emu2413: 9 HH, 10 CYM, 11 TOM, 12 SD, 13 BD.
        public static readonly string[] RhythmNames = ["hh", "cym", "tom", "sd", "bd"];

        public uint DebugMask => mask;

        public FmState InspectState(byte audioControl, bool fmAudible, bool psgAudible)
        {
            var rhythm = (reg[0x0e] & 0x20) != 0;
            var channels = Enumerable.Range(0, 9).Select(ch =>
            {
                var fnum = reg[0x10 + ch] | (reg[0x20 + ch] & 1) << 8;
                var block = (reg[0x20 + ch] >> 1) & 7;
                var instrument = reg[0x30 + ch] >> 4;
                return new FmChannelState(ch + 1, instrument, InstrumentNames[instrument], reg[0x30 + ch] & 15, fnum, block,
                    (reg[0x20 + ch] & 0x10) != 0, (reg[0x20 + ch] & 0x20) != 0, fnum * (clk / 72.0) / (1 << (19 - block)));
            }).ToArray();
            // r14 bits: 0 HH, 1 CYM, 2 TOM, 3 SD, 4 BD.
            var keys = rhythm ? RhythmNames.Where((_, bit) => (reg[0x0e] & (1 << bit)) != 0).ToArray() : [];
            var muted = Enumerable.Range(0, 14).Where(bit => (mask & (1u << bit)) != 0)
                .Select(bit => bit < 9 ? (bit + 1).ToString() : RhythmNames[bit - 9]).ToArray();
            return new(audioControl, fmAudible, psgAudible, (byte)adr, (byte[])reg.Clone(), rhythm, keys, channels, muted);
        }
    }
}
