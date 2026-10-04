namespace Zafiro.MasterSystem.Debug.Tests;

internal static class TestRom
{
    public static byte[] Program(params byte[] code)
    {
        var rom = new byte[65536];
        code.CopyTo(rom, 0);
        return rom;
    }

    public static byte[] DisplayAndSound(bool gameGear = false)
    {
        var rom = Program(0xC3, 0x00, 0x01);
        var code = new List<byte> { 0xF3, 0x31, 0xF0, 0xDF };
        void Out(byte value, byte port) => code.AddRange([0x3E, value, 0xD3, port]);
        void Register(byte register, byte value) { Out(value, 0xBF); Out((byte)(0x80 | register), 0xBF); }
        void Vram(ushort address) { Out((byte)address, 0xBF); Out((byte)(0x40 | (address >> 8)), 0xBF); }
        Register(0, 4); Register(1, 0x40); Register(2, 0x0E); Register(5, 0x7E); Register(6, 0);
        Vram(0);
        for (var y = 0; y < 8; y++) { Out(0xFF, 0xBE); Out(0, 0xBE); Out(0, 0xBE); Out(0, 0xBE); }
        Vram(0x3F00); Out(0xD0, 0xBE);
        Out((byte)(gameGear ? 2 : 1), 0xBF); Out(0xC0, 0xBF); Out((byte)(gameGear ? 15 : 3), 0xBE);
        if (gameGear) Out(0, 0xBE);
        Out(0x8E, 0x7F); Out(0x0F, 0x7F); Out(0x90, 0x7F);
        var loop = (ushort)(0x100 + code.Count);
        code.AddRange([0x21, 0x00, 0xC0, 0x34, 0xDB, 0xDC, 0x32, 0x01, 0xC0, 0xDB, 0, 0x32, 2, 0xC0, 0xC3, (byte)loop, (byte)(loop >> 8)]);
        code.ToArray().CopyTo(rom, 0x100);
        return rom;
    }

    // Streams 64 bytes to the name table, then updates 256 RAM bytes, forever; display on, no interrupts.
    public static byte[] StreamingVram()
    {
        var rom = Program(0xF3, 0x31, 0xF0, 0xDF, 0xC3, 0x00, 0x01);
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
}
