using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sms.Debug.Emulator;

// Keep stdout exclusively for MCP JSON-RPC, including diagnostics from dependencies.
Console.SetOut(Console.Error);
var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<SmsDebugSession>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
