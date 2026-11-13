using System.Diagnostics;
using System.Text.Json;
using Azure.AI.Projects;
using LeakOps.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace LeakOps.Agents;

/// <summary>Everything that defines one agent: its name, its instructions and the MCP tools it may use (and nothing else).</summary>
public record AgentSpec(string Name, string Instructions, IReadOnlyCollection<string> AllowedTools);

/// <summary>
/// Shared plumbing for all six agents. It connects to the MCP server, hands the agent only the tools on its allow-list,
/// runs it on the Foundry project's model and returns a typed, schema-checked answer together with a trace of what the agent did.
/// </summary>
public class LeakOpsAgentRuntime(AIProjectClient project, AgentSettings settings, ILogger<LeakOpsAgentRuntime> logger)
{
    // Web defaults = camelCase, matching the JSON schema the model is asked to follow.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentResult<T>> RunAsync<T>(AgentSpec spec, string input, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // "await using" = Python's "async with": the MCP connection is closed when the method ends.
        await using var mcp = await ConnectMcpAsync(cancellationToken);
        var available = await mcp.ListToolsAsync(cancellationToken: cancellationToken);
        var tools = spec.AllowedTools
            .Select(name => available.FirstOrDefault(t => t.Name == name)
                ?? throw new InvalidOperationException($"MCP tool '{name}' needed by {spec.Name} is not offered by {settings.McpServerUrl}."))
            .Cast<AITool>()
            .ToList();

        // Code-defined Foundry agent: no server-side agent resource is created, the app owns model, instructions and tools.
        AIAgent agent = project
            .AsAIAgent(model: settings.ModelDeployment, name: spec.Name, instructions: spec.Instructions, tools: tools)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: spec.Name)
            .Build();

        var response = await agent.RunAsync<T>(input, serializerOptions: JsonOptions, cancellationToken: cancellationToken);

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => new ToolCallTrace(c.Name, JsonSerializer.Serialize(c.Arguments, JsonOptions)))
            .ToList();

        var trace = new AgentTrace(spec.Name, settings.ModelDeployment, stopwatch.ElapsedMilliseconds,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, calls);
        logger.LogInformation("{Agent} finished in {Ms} ms with {Calls} tool call(s): {Tools}",
            spec.Name, trace.DurationMs, calls.Count, string.Join(", ", calls.Select(c => c.Name)));

        return new AgentResult<T>(response.Result, trace);
    }

    private async Task<McpClient> ConnectMcpAsync(CancellationToken cancellationToken)
    {
        var options = new HttpClientTransportOptions { Endpoint = settings.McpServerUrl };
        if (!string.IsNullOrWhiteSpace(settings.McpAuthKey))
            options.AdditionalHeaders = new Dictionary<string, string> { ["x-functions-key"] = settings.McpAuthKey };

        return await McpClient.CreateAsync(new HttpClientTransport(options), cancellationToken: cancellationToken);
    }
}
