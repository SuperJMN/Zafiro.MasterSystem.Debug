using System.Globalization;
using System.Text.RegularExpressions;

namespace Sms.Debug.Core;

public sealed partial class Condition
{
    private readonly string operand, comparison;
    private readonly int value;
    private static readonly HashSet<string> Registers = new(StringComparer.OrdinalIgnoreCase)
        { "A", "F", "B", "C", "D", "E", "H", "L", "AF", "BC", "DE", "HL", "IX", "IY", "SP", "PC", "I", "R", "SCANLINE", "VCOUNTER", "HCOUNTER", "FRAME" };

    private Condition(string operand, string comparison, int value)
    {
        this.operand = operand.ToUpperInvariant();
        this.comparison = comparison;
        this.value = value;
    }

    public static Condition Parse(string expression)
    {
        var match = Expression().Match(expression);
        if (!match.Success) throw new ArgumentException("Expected REGISTER or [address/register] followed by ==, !=, <, <=, >, >= and an integer.");
        var operand = match.Groups[1].Value.ToUpperInvariant();
        var name = operand.Trim('[', ']');
        if (!Registers.Contains(name) && !(operand.StartsWith('[') && TryNumber(name, out _)))
            throw new ArgumentException($"Unknown condition operand: {operand}.");
        if (operand.StartsWith('[') && TryNumber(name, out var address) && address is < 0 or > 65535)
            throw new ArgumentException("Condition memory address must be 0..65535.");
        return new(operand, match.Groups[2].Value, Number(match.Groups[3].Value));
    }

    public bool Evaluate(CpuRegisters cpu, VdpState vdp, long frame, Func<ushort, byte> memory) => Compile(name => name switch
    {
        "SCANLINE" => () => vdp.Scanline, "VCOUNTER" => () => vdp.VCounter, "HCOUNTER" => () => vdp.HCounter, "FRAME" => () => frame,
        _ => () => Register(cpu, name)
    }, memory)();

    /// <summary>
    /// Binds the condition to live state once. The result reads only its operand on each call: the
    /// reader <paramref name="register"/> returns for an upper-case register name (or SCANLINE,
    /// VCOUNTER, HCOUNTER, FRAME), or a <paramref name="memory"/> byte.
    /// </summary>
    public Func<bool> Compile(Func<string, Func<long>> register, Func<ushort, byte> memory)
    {
        Func<long> actual;
        if (!operand.StartsWith('[')) actual = register(operand);
        else if (TryNumber(operand[1..^1], out var number)) { var address = checked((ushort)number); actual = () => memory(address); }
        else { var pointer = register(operand[1..^1]); actual = () => memory(checked((ushort)pointer())); }
        var expected = value;
        return comparison switch
        {
            "==" => () => actual() == expected, "!=" => () => actual() != expected, "<" => () => actual() < expected,
            "<=" => () => actual() <= expected, ">" => () => actual() > expected, ">=" => () => actual() >= expected,
            _ => () => false
        };
    }

    private static long Register(CpuRegisters cpu, string name) => name switch
    {
        "A" => cpu.A, "F" => cpu.F, "B" => cpu.B, "C" => cpu.C, "D" => cpu.D, "E" => cpu.E, "H" => cpu.H, "L" => cpu.L,
        "AF" => cpu.AF, "BC" => cpu.BC, "DE" => cpu.DE, "HL" => cpu.HL, "IX" => cpu.IX, "IY" => cpu.IY,
        "SP" => cpu.SP, "PC" => cpu.PC, "I" => cpu.I, "R" => cpu.R,
        _ => throw new ArgumentException($"Unknown register: {name}.")
    };

    public object Describe() => new
    {
        operand = operand.StartsWith('[') && TryNumber(operand[1..^1], out var address) ? $"[{address}]" : operand,
        comparison,
        value
    };

    public static int Number(string text)
    {
        text = text.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith('$');
        return int.Parse(hex ? text[(text[0] == '$' ? 1 : 2)..] : text,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static bool TryNumber(string text, out int number)
    {
        try { number = Number(text); return true; }
        catch (Exception ex) when (ex is FormatException or OverflowException) { number = 0; return false; }
    }

    [GeneratedRegex(@"^\s*(\[[A-Za-z0-9$x]+\]|[A-Za-z]+)\s*(==|!=|<=|>=|<|>)\s*(0[xX][0-9a-fA-F]+|\$[0-9a-fA-F]+|[0-9]+)\s*$")]
    private static partial Regex Expression();
}
