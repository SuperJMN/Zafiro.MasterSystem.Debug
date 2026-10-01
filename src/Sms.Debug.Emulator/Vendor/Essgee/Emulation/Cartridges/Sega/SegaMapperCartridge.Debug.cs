namespace Essgee.Emulation.Cartridges.Sega;
public partial class SegaMapperCartridge
{
    public int DebugBankAt(ushort address) => address < 0x400 ? 0 : address < 0x4000 ? romBank0 : address < 0x8000 ? romBank1 : isRamEnabled ? -1 : romBank2;
}
