namespace Sms.Debug.Core;

public sealed record CpuRegisters(ushort AF, ushort BC, ushort DE, ushort HL, ushort IX, ushort IY,
    ushort SP, ushort PC, ushort AlternateAF, ushort AlternateBC, ushort AlternateDE, ushort AlternateHL,
    byte I, byte R, bool IFF1, bool IFF2, byte InterruptMode, bool Halted)
{
    public byte A => (byte)(AF >> 8);
    public byte F => (byte)AF;
    public byte B => (byte)(BC >> 8);
    public byte C => (byte)BC;
    public byte D => (byte)(DE >> 8);
    public byte E => (byte)DE;
    public byte H => (byte)(HL >> 8);
    public byte L => (byte)HL;
}

public sealed record VdpState(byte[] Registers, byte Status, int Scanline, int VCounter, int HCounter,
    int Width, int Height, int NametableHeight, ushort NameTableAddress, ushort SpriteTableAddress,
    ushort SpritePatternAddress, ushort Address, byte Code, bool ControlLatchPending, bool InterruptPending,
    bool Mode4, bool DisplayEnabled, bool FrameInterruptPending, bool LineInterruptPending);
public sealed record PsgState(ushort[] TonePeriods, ushort[] Attenuation, ushort NoiseControl,
    ushort NoiseShiftRegister, double[] ToneFrequencies, int SampleRate);
public sealed record FmChannelState(int Channel, int Instrument, string InstrumentName, int Attenuation,
    int FNumber, int Block, bool KeyOn, bool Sustain, double FrequencyHz);
public sealed record FmState(byte AudioControl, bool FmAudible, bool PsgAudible, byte AddressLatch, byte[] Registers,
    bool RhythmMode, string[] RhythmKeysOn, FmChannelState[] Channels, string[] MutedChannels);
public sealed record SessionState(bool Loaded, string? RomPath, string? RomSha256, string Mapper,
    string System, int[] RomBanks, bool CartridgeRamEnabled, string Standard, bool FmUnit, long Cycles, long Instructions,
    long Frames, CpuRegisters? Cpu, VdpState? Vdp);
public sealed record Breakpoint(int Id, ushort Address, string? Condition, int? Bank);
public sealed record Watchpoint(int Id, string Space, int Start, int End, string Access);
public sealed record BusAccess(string Space, int Address, byte Value, string Access, ushort PC,
    int Bank, long Cycle, long Frame, int Scanline);
public sealed record RunResult(string Reason, long InstructionsExecuted, long CyclesExecuted,
    long FramesExecuted, int? BreakpointId, int? WatchpointId, BusAccess? Access, SessionState State);
public sealed record MemoryBlock(string Space, int Address, byte[] Bytes, string Hex);
public sealed record DisassembledInstruction(ushort Address, int Bank, byte[] Bytes, string Mnemonic);
public sealed record SpriteInfo(int Index, int X, int Y, int Pattern, bool Terminator, bool AfterTerminator);
public sealed record TileInfo(int Index, int Tile, bool FlipX, bool FlipY, int Palette, bool Priority, ushort Raw);
public sealed record InputSegment(int Frames, string Buttons = "", int Player = 1);
public sealed record Observation(long Frame, CpuRegisters Cpu, VdpState Vdp, PsgState Psg, FmState? Fm,
    MemoryBlock[] Memory, string ScreenSha256);
public sealed record ObservedExecution(Observation[] Samples, RunResult Result);
public sealed record WriteTrace(BusAccess[] Writes, bool Truncated, RunResult Result);
public sealed record AudioCapture(byte[] Wav, short[] Samples, double Rms, int Peak, RunResult Result);
public sealed record SymbolInfo(string Name, ushort Address, int? Bank);
