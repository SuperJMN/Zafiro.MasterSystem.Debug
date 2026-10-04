using Zafiro.MasterSystem.Debug.Core;

namespace Zafiro.MasterSystem.Debug.Emulator;

internal enum BusSpace : byte { Cpu, Ram, Io, Vram, Cram, Vdp }

// A write the run loop should keep; called only for writes, with the machine paused at the access.
internal delegate void WriteSink(BusSpace space, int address, byte value);

// Observes bus accesses while a run is active. Nothing is allocated per access: a BusAccess is
// built only for a watchpoint hit or a kept trace entry, and last writers live in fixed tables.
internal sealed class BusMonitor
{
    private static readonly string[] Names = ["cpu", "ram", "io", "vram", "cram", "vdp"];
    private static readonly int[] Sizes = [65536, 8192, 256, 16384, 64, 16];
    private static readonly Range[] None = [];
    private readonly LastWrite[]?[] lastWriters = new LastWrite[]?[Names.Length];
    // Watch ranges per space and access kind (index space * 2 + write), in watchpoint list order.
    private Range[][] watches = Enumerable.Repeat(None, Names.Length * 2).ToArray();
    private SmsMachine? machine;
    private WriteSink? trace;

    public bool ObserveReads { get; private set; }
    public int? HitWatch { get; private set; }
    public BusAccess? HitAccess { get; private set; }

    public static string Name(BusSpace space) => Names[(int)space];

    public static BusSpace? Parse(string space) => Array.IndexOf(Names, space) is var index and >= 0 ? (BusSpace)index : null;

    public void SetWatchpoints(IEnumerable<Watchpoint> watchpoints)
    {
        var lists = Enumerable.Range(0, Names.Length * 2).Select(_ => new List<Range>()).ToArray();
        foreach (var wp in watchpoints)
        {
            var space = (int)Parse(wp.Space)!.Value;
            if (wp.Access is "read" or "readwrite") lists[space * 2].Add(new(wp.Start, wp.End, wp.Id));
            if (wp.Access is "write" or "readwrite") lists[space * 2 + 1].Add(new(wp.Start, wp.End, wp.Id));
        }
        watches = lists.Select(l => l.Count == 0 ? None : l.ToArray()).ToArray();
        ObserveReads = watches.Where((_, index) => index % 2 == 0).Any(l => l.Length != 0);
    }

    public void Attach(SmsMachine target, WriteSink? sink)
    {
        machine = target; trace = sink; HitWatch = null; HitAccess = null;
        target.Monitor = this;
    }

    public void Detach()
    {
        if (machine != null) machine.Monitor = null;
        machine = null; trace = null;
    }

    public void OnAccess(BusSpace space, int address, byte value, bool write)
    {
        var m = machine!;
        if (write)
        {
            Record(m, space, space, address, value);
            // CPU RAM mirrors identify the same physical write.
            if (space == BusSpace.Cpu && address >= 0xC000) Record(m, BusSpace.Ram, space, address, value);
            trace?.Invoke(space, address, value);
        }
        if (HitWatch != null) return;
        foreach (var range in watches[(int)space * 2 + (write ? 1 : 0)])
            if (address >= range.Start && address <= range.End)
            { HitWatch = range.Id; HitAccess = m.Describe(space, address, value, write); return; }
    }

    private void Record(SmsMachine m, BusSpace table, BusSpace space, int address, byte value)
    {
        var entries = lastWriters[(int)table] ??= new LastWrite[Sizes[(int)table]];
        entries[table == BusSpace.Ram ? address & 0x1FFF : address] = new(m.Cycles, m.Frames, (ushort)address, m.InstructionPc,
            (short)m.InstructionBank, (short)m.Vdp.CurrentScanline, value, (byte)(space + 1));
    }

    public BusAccess? LastWriter(string space, int address)
    {
        if (Parse(space) is not { } table || lastWriters[(int)table] is not { } entries || address >= entries.Length) return null;
        var e = entries[address];
        return e.Space == 0 ? null : new(Names[e.Space - 1], e.Address, e.Value, "write", e.Pc, e.Bank, e.Cycle, e.Frame, e.Scanline);
    }

    public void ClearLastWriters()
    {
        foreach (var entries in lastWriters) if (entries != null) Array.Clear(entries);
    }

    private readonly record struct Range(int Start, int End, int Id);

    // Space is the access's BusSpace + 1, so a default entry means no write was seen.
    private readonly record struct LastWrite(long Cycle, long Frame, ushort Address, ushort Pc, short Bank, short Scanline,
        byte Value, byte Space);
}
