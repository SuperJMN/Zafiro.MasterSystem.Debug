using System.Diagnostics;
using System.Text.Json;
using Sms.Debug.Mcp;

namespace Sms.Debug.Tests;

public sealed class StdioTests
{
    [Fact]
    public async Task Real_stdio_server_discovers_tools_executes_rom_and_returns_images_audio_and_errors()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "sms-debug-mcp.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var server = Path.Combine(root.FullName, "src/Sms.Debug.Mcp/bin", configuration, "net10.0/Sms.Mcp.dll");
        var temp = Path.Combine(Path.GetTempPath(), "sms-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var rom = Path.Combine(temp, "fixture.sms");
        await File.WriteAllBytesAsync(rom, TestRom.DisplayAndSound());
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = temp
        };
        start.ArgumentList.Add(server);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = 0;
        async Task<JsonElement> Request(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++id, method, @params = parameters }));
            await process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                using var document = JsonDocument.Parse(line);
                var reply = document.RootElement;
                if (!reply.TryGetProperty("id", out var received) || received.GetInt32() != id) continue;
                Assert.False(reply.TryGetProperty("error", out _), line);
                return reply.GetProperty("result").Clone();
            }
        }
        Task<JsonElement> Call(string name, object arguments) => Request("tools/call", new { name, arguments });
        static JsonElement Payload(JsonElement reply)
        {
            Assert.False(reply.TryGetProperty("isError", out var error) && error.GetBoolean(), reply.ToString());
            return JsonDocument.Parse(reply.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        }
        try
        {
            await Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync(timeout.Token);
            var list = await Request("tools/list", new { });
            var names = list.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
            Assert.Contains("capture_audio", names); Assert.Contains("set_breakpoint", names); Assert.Contains("dump_tileset", names); Assert.Contains("read_fm_state", names);
            Assert.True(Payload(await Call("load_rom", new { path = rom })).GetProperty("loaded").GetBoolean());
            Assert.Equal(2, Payload(await Call("run_frame", new { frames = 2 })).GetProperty("framesExecuted").GetInt64());
            var image = (await Call("capture_screen", new { })).GetProperty("content")[0];
            Assert.Equal("image", image.GetProperty("type").GetString());
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            var audio = Payload(await Call("capture_audio", new { frames = 1 }));
            Assert.True(audio.GetProperty("peak").GetInt32() > 0);
            var error = await Call("read_memory", new { address = "65535", length = 2 });
            Assert.True(error.GetProperty("isError").GetBoolean());
            var state = Payload(await Call("get_state", new { }));
            Assert.True(state.GetProperty("loaded").GetBoolean());
            Assert.True(state.GetProperty("fmUnit").GetBoolean());
            Assert.Equal(0, Payload(await Call("read_fm_state", new { })).GetProperty("audioControl").GetInt32());
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            _ = await stderr;
            Directory.Delete(temp, recursive: true);
        }
    }
}
