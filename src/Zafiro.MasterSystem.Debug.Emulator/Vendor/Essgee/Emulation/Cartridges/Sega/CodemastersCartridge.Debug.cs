namespace Essgee.Emulation.Cartridges.Sega;
public partial class CodemastersCartridge
{
    public int DebugBankAt(ushort address) => isRamEnabled && address >= 0xA000 ? -1 : pagingRegisters[address >> 14];
}
