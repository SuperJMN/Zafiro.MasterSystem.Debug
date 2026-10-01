using System.Reflection;
using System.Text.Json;

namespace Sms.Debug.Emulator;

// Version-bound snapshots capture chip phase, private latches, sprite evaluation buffers and
// partially generated audio/video as well as registers. Delegates remain wired to the new machine.
internal static class MachineSnapshot
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    private static IEnumerable<FieldInfo> Fields(object obj)
    {
        for (var type = obj.GetType(); type != null; type = type.BaseType)
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (typeof(Delegate).IsAssignableFrom(field.FieldType) || field.Name is "romData" or "Rom") continue;
            var t = field.FieldType;
            if (t.IsValueType || t == typeof(string) || t.IsArray || t == typeof(List<short>)) yield return field;
        }
    }

    private static string Key(FieldInfo field) => field.DeclaringType!.FullName + ":" + field.Name;

    private static Dictionary<string, JsonElement> CaptureObject(object obj) => Fields(obj).ToDictionary(Key,
        f => JsonSerializer.SerializeToElement(f.GetValue(obj), f.FieldType, Options));

    public static Dictionary<string, Dictionary<string, JsonElement>> Capture(SmsMachine machine)
    {
        var data = new Dictionary<string, Dictionary<string, JsonElement>>
        {
            ["machine"] = CaptureObject(machine), ["cpu"] = CaptureObject(machine.Cpu),
            ["vdp"] = CaptureObject(machine.Vdp), ["psg"] = CaptureObject(machine.Psg),
            ["cartridge"] = CaptureObject(machine.Cartridge)
        };
        if (machine.Fm != null) data["fm"] = CaptureObject(machine.Fm);
        return data;
    }

    private static void RestoreObject(object obj, Dictionary<string, JsonElement> values)
    {
        var fields = Fields(obj).ToArray();
        if (fields.Length != values.Count) throw new ArgumentException("Snapshot fields do not match this backend version.");
        foreach (var field in fields)
        {
            var value = values[Key(field)].Deserialize(field.FieldType, Options);
            if (value is null) throw new ArgumentException("Snapshot contains a null field.");
            if (field.IsInitOnly)
            {
                if (field.GetValue(obj) is Array destination && value is Array source)
                {
                    if (destination.Length != source.Length) throw new ArgumentException("Snapshot array size mismatch.");
                    Array.Copy(source, destination, source.Length);
                }
                else if (!Equals(field.GetValue(obj), value)) throw new ArgumentException("Snapshot configuration mismatch.");
            }
            else field.SetValue(obj, value);
        }
    }

    public static void Restore(SmsMachine m, Dictionary<string, Dictionary<string, JsonElement>> data)
    {
        RestoreObject(m, data["machine"]); RestoreObject(m.Cpu, data["cpu"]);
        RestoreObject(m.Vdp, data["vdp"]); RestoreObject(m.Psg, data["psg"]);
        RestoreObject(m.Cartridge, data["cartridge"]);
        if (m.Fm != null) RestoreObject(m.Fm, data["fm"]);
    }
}
