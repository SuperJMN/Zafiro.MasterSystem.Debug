namespace Zafiro.MasterSystem.Debug.Mcp;

internal static class Artifacts
{
    public static string Resolve(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || Path.GetExtension(path) != extension)
            throw new ArgumentException($"Artifact path must be relative and end in {extension}.");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or "..")) throw new ArgumentException("Artifact path contains an invalid component.");
        var current = Directory.GetCurrentDirectory();
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (new FileInfo(current).LinkTarget != null || new DirectoryInfo(current).LinkTarget != null)
                throw new ArgumentException("Artifact paths cannot traverse symbolic links.");
        }
        if (File.Exists(current) || Directory.Exists(current))
            throw new IOException("Artifact path already exists; choose a new filename.");
        return current;
    }

    public static object Write(string path, string extension, byte[] bytes)
    {
        var full = Resolve(path, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Never silently overwrite the user's artifacts.
        using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        output.Write(bytes);
        return new { Path = path, Bytes = bytes.Length };
    }
}
