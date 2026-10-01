using System.Text;

namespace Sms.Debug.Core;

public static class WavEncoder
{
    public static byte[] Encode(short[] samples, int sampleRate, int channels)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return stream.ToArray();
    }
}
