using Emu2413;
using Essgee.Emulation;
using Essgee.Emulation.Audio;
using Essgee.Emulation.Cartridges;
using Essgee.Emulation.Cartridges.Sega;
using Essgee.Emulation.CPU;
using Essgee.Emulation.Video;
using Sms.Debug.Core;

namespace Sms.Debug.Emulator;

internal sealed class SmsMachine
{
    public const int SampleRate = 44100;
    public readonly Z80A Cpu;
    public readonly SegaSMSVDP Vdp;
    public readonly SN76489 Psg;
    public readonly ICartridge Cartridge;
    public readonly byte[] Ram = new byte[8192];
    public readonly byte[] Rom;
    public readonly string Mapper;
    public readonly string Standard;
    public readonly bool GameGear;
    // Mark III FM unit / Japanese SMS YM2413; never present on Game Gear.
    public readonly Opll? Fm;
    public byte FmControl;
    public readonly byte[] Controllers = [0, 0];
    public byte MemoryControl = 0xA8;
    public byte IoControl = 0xFF;
    public byte HCounterLatch;
    public bool PausePressed;
    public bool ResetPressed;
    public long Cycles, Instructions, Frames;
    public ushort InstructionPc;
    public int InstructionBank;
    // Set only while a run observes the bus; a reference, so snapshots never capture it.
    public BusMonitor? Monitor;

    public SmsMachine(byte[] rom, string mapper, string standard, bool gameGear, bool fm)
    {
        Rom = rom;
        Mapper = mapper;
        Standard = standard;
        GameGear = gameGear;
        // Padding to a power of two keeps the mapper mask valid for short/odd-sized ROMs.
        var size = 16384;
        while (size < rom.Length) size *= 2;
        Cartridge = mapper == "codemasters" ? new CodemastersCartridge(size, 32768)
            : new SegaMapperCartridge(size, 32768);
        Cartridge.LoadRom(rom);
        Vdp = gameGear ? new SegaGGVDP() : new SegaSMSVDP();
        Psg = gameGear ? new SegaGGPSG() : new SegaSMSPSG();
        var clock = standard == "pal" ? 10640684.0 : 10738635.0;
        var refresh = standard == "pal" ? 49.701459 : 59.922743;
        Vdp.SetRevision(1);
        Vdp.SetClockRate(clock / 2);
        Vdp.SetRefreshRate(refresh);
        Psg.SetSampleRate(SampleRate);
        Psg.SetOutputChannels(gameGear ? 2 : 1);
        Psg.SetClockRate(clock / 3);
        Psg.SetRefreshRate(refresh);
        Cpu = new Z80A(ReadCpu, WriteCpu, ReadPort, WritePort);
        Cpu.Startup();
        Vdp.Startup();
        Psg.Startup();
        Vdp.FrameRendered = () => Frames++;
        if (fm && !gameGear)
        {
            // The YM2413 shares the Z80 clock; it is advanced once per PSG output sample so both chips stay in phase.
            Fm = new Opll((uint)Math.Round(clock / 3), SampleRate);
            Psg.MonoSampleObserver = MixSample;
        }
    }

    public bool FmPresent => Fm != null;
    // Japanese SMS audio control (port F2, bits 0-1): 0 PSG, 1 FM, 2 silent, 3 PSG and FM.
    public bool PsgAudible => Fm == null || (FmControl & 3) is 0 or 3;
    public bool FmAudible => Fm != null && (FmControl & 3) is 1 or 3;
    // Balances loudness: Wonder Boy in Monster Land's FM and PSG soundtracks reach comparable RMS at this gain.
    private const int FmGain = 4;
    private Action<short[]>? sampleObserver;

    public Action<short[]>? SampleObserver
    {
        get => sampleObserver;
        set
        {
            sampleObserver = value;
            if (Fm == null) Psg.SampleObserver = value;
        }
    }

    private void MixSample(short psg)
    {
        var fm = Fm!.Calc();
        if (sampleObserver == null) return;
        var mixed = (PsgAudible ? psg : 0) + (FmAudible ? fm * FmGain : 0);
        sampleObserver([(short)Math.Clamp(mixed, short.MinValue, short.MaxValue)]);
    }

    public int Channels => GameGear ? 2 : 1;

    public int BankAt(ushort address)
    {
        if (address >= 0xC000) return -1;
        if (Mapper == "sega" && address < 0x400) return 0;
        // Mapper state is exposed by the vendored cartridge's debugger extension.
        return Cartridge switch
        {
            SegaMapperCartridge sega => sega.DebugBankAt(address),
            CodemastersCartridge cm => cm.DebugBankAt(address),
            _ => -1
        };
    }

    public byte PeekCpu(ushort address) => address >= 0xC000
        ? ((MemoryControl & 0x10) == 0 ? Ram[address & 0x1FFF] : (byte)0xFF)
        : ((MemoryControl & 0x40) == 0 ? Cartridge.Read(address) : (byte)0xFF);

    public BusAccess Describe(BusSpace space, int address, byte value, bool write) => new(BusMonitor.Name(space), address, value,
        write ? "write" : "read", InstructionPc, InstructionBank, Cycles, Frames, Vdp.CurrentScanline);

    private void Write(BusSpace space, int address, byte value) => Monitor?.OnAccess(space, address, value, true);

    // Reads matter only to read watchpoints, so opcode and operand fetches skip the monitor otherwise.
    private void Read(BusSpace space, int address, byte value)
    {
        if (Monitor is { ObserveReads: true } monitor) monitor.OnAccess(space, address, value, false);
    }

    private byte ReadCpu(ushort address)
    {
        var value = PeekCpu(address);
        Read(BusSpace.Cpu, address, value);
        return value;
    }

    public void PokeCpu(ushort address, byte value)
    {
        if ((MemoryControl & 0x40) == 0) Cartridge.Write(address, value);
        if (address >= 0xC000 && (MemoryControl & 0x10) == 0) Ram[address & 0x1FFF] = value;
    }

    private void WriteCpu(ushort address, byte value)
    {
        PokeCpu(address, value);
        Write(BusSpace.Cpu, address, value);
    }

    private byte ControllerPort(bool second)
    {
        if ((MemoryControl & 4) != 0) return 0xFF;
        var value = second ? 0xFF & ~((Controllers[1] >> 2) & 15) : 0xFF & ~(Controllers[0] | (Controllers[1] << 6));
        if (second && ResetPressed && !GameGear) value &= ~16;
        // Export-region TR/TH pins; direction bits 0..3, output levels 4..7.
        if (!second)
        {
            if ((IoControl & 1) == 0) value = (value & ~32) | ((IoControl & 16) << 1);
        }
        else
        {
            if ((IoControl & 4) == 0) value = (value & ~8) | ((IoControl & 64) >> 3);
            if ((IoControl & 2) == 0) value = (value & ~64) | ((IoControl & 32) << 1);
            if ((IoControl & 8) == 0) value = (value & ~128) | (IoControl & 128);
        }
        return (byte)value;
    }

    public byte ReadPort(byte port)
    {
        byte value;
        if (GameGear && port < 7)
            value = port == 0 ? (byte)(PausePressed ? 0x40 : 0xC0) : (byte)0xFF;
        // FM detection: bit 0 returns the latched FM enable, bits 1-2 read as zero.
        else if (Fm != null && port == 0xF2) value = (byte)(0xF8 | (FmControl & 1));
        else value = (port & 0xC1) switch
        {
            0x40 => Vdp.ReadPort(0x40),
            0x41 => HCounterLatch,
            0x80 => Vdp.ReadPort(0x80),
            0x81 => Vdp.ReadPort(0x81),
            0xC0 => ControllerPort(false),
            0xC1 => ControllerPort(true),
            _ => 0xFF
        };
        Read(BusSpace.Io, port, value);
        return value;
    }

    public void WritePort(byte port, byte value)
    {
        if (GameGear && port < 7)
        {
            if (port == 6) Psg.WritePort(6, value);
        }
        else if (Fm != null && port is 0xF0 or 0xF1 or 0xF2)
        {
            if (port == 0xF2) FmControl = (byte)(value & 3);
            else Fm.WriteIO(port, value);
        }
        else switch (port & 0xC1)
        {
            case 0: MemoryControl = value; break;
            case 1:
                if (((~IoControl & value) & 0xA0) != 0) HCounterLatch = Vdp.ReadPort(0x41);
                IoControl = value;
                break;
            case 0x40: case 0x41: Psg.WritePort(port, value); break;
            case 0x80: case 0x81:
                var code = Vdp.DebugCode; var address = Vdp.DebugAddress; var latchPending = Vdp.DebugControlLatchPending;
                Vdp.WritePort(port, value);
                if ((port & 1) == 0)
                    Write(code == 3 ? BusSpace.Cram : BusSpace.Vram, address & (code == 3 ? (GameGear ? 63 : 31) : 16383), value);
                else if (latchPending && (value & 0xC0) == 0x80 && (value & 15) < 11)
                    Write(BusSpace.Vdp, value & 15, Vdp.DebugRegisters[value & 15]);
                break;
        }
        Write(BusSpace.Io, port, value);
    }

    public int Step()
    {
        InstructionPc = Cpu.DebugPc;
        InstructionBank = BankAt(InstructionPc);
        Cpu.SetInterruptLine(InterruptType.Maskable, Vdp.InterruptLine);
        var cycles = Cpu.Step();
        if (cycles <= 0) throw new InvalidOperationException("CPU returned a nonpositive cycle count.");
        // Preserve the half VDP-cycle across instructions using the cumulative CPU cycle count.
        var videoCycles = (int)(((Cycles + cycles) * 3 / 2) - (Cycles * 3 / 2));
        Vdp.Step(videoCycles);
        Psg.Step(cycles);
        Cycles += cycles;
        Instructions++;
        return cycles;
    }

    public void SetController(int player, byte buttons, bool pause, bool reset)
    {
        Controllers[player - 1] = buttons;
        if (player == 1)
        {
            if (pause && !PausePressed && !GameGear) Cpu.SetInterruptLine(InterruptType.NonMaskable, InterruptState.Assert);
            PausePressed = pause;
            ResetPressed = reset;
        }
    }
}
