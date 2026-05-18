using System;
using System.Threading.Tasks;
using AtcMcpServer.Configuration;
using AtcMcpServer.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AtcMcpServer;

/// <summary>
/// Entry point. Loads the airport configuration from environment variables,
/// fails fast on any invalid value (R2b), then starts the MCP server over
/// stdio. Tools and resources are discovered from this assembly automatically.
///
/// Why stdio: it's the Anthropic-supported transport for local clients
/// (Claude Desktop, mcp-inspector). For remote / browser-based clients the
/// SDK also supports HTTP — see README "How to connect" for both paths.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Load + validate config BEFORE wiring DI so a bad env fails clearly,
        // synchronously, with a precise message — and a non-zero exit code.
        AirportConfig config;
        try
        {
            config = AirportConfigLoader.LoadFromEnvironment();
        }
        catch (ConfigurationException ex)
        {
            await Console.Error.WriteLineAsync($"[atc-mcp] FATAL: configuration invalid — {ex.Message}");
            return 78; // EX_CONFIG (sysexits.h)
        }

        // The MCP host. stdio transport means stdin/stdout is the JSON-RPC channel;
        // logs MUST go to stderr (otherwise they would corrupt the protocol stream).
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o =>
        {
            o.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // AirportState is the single shared object. DI hands it to every tool/resource.
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<AirportState>(_ => new AirportState(config));

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}
