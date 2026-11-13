using System.ClientModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Core;
using Azure.Identity;
using LeakOps.Agents;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

// Creates (or updates) the six LeakOps agents in the Foundry project, so they show up in the portal at https://ai.azure.com
// and can be tested, traced and evaluated there. Rerunnable: a new agent version is only created when the definition changed.
//
// Settings (environment variables):
//   AZURE_AI_PROJECT_ENDPOINT  required  the Foundry project endpoint
//   MCP_SERVER_URL             required  public URL of the MCP server (Foundry calls it server-side)
//   MODEL_DEPLOYMENT_NAME      optional  default gpt-5.4
//   MCP_CONNECTION_NAME        optional  project connection that holds the MCP key, default leakops-mcp
//   AZURE_TENANT_ID            optional  tenant for the Azure CLI sign-in
//   --list                     print the agents in the project and exit
//   --delete                   delete the six LeakOps agents and exit
static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");

var endpoint = Required("AZURE_AI_PROJECT_ENDPOINT");
var model = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT_NAME") is { Length: > 0 } m ? m : "gpt-5.4";
var connection = Environment.GetEnvironmentVariable("MCP_CONNECTION_NAME") is { Length: > 0 } c ? c : "leakops-mcp";
var tenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");

TokenCredential credential = new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenant });
var admin = new AIProjectClient(new Uri(endpoint), credential).AgentAdministrationClient;

async Task<ProjectsAgentVersion?> LatestVersionAsync(string name)
{
    ProjectsAgentVersion? latest = null;
    try
    {
        await foreach (var version in admin.GetAgentVersionsAsync(name))
            if (latest is null || version.CreatedAt > latest.CreatedAt) latest = version;
    }
    catch (ClientResultException e) when (e.Status == 404) { }
    return latest;
}

if (args.Contains("--list"))
{
    await foreach (var agent in admin.GetAgentsAsync())
        Console.WriteLine($"{agent.Name}  latest version={(await LatestVersionAsync(agent.Name))?.Version}");
    return;
}

if (args.Contains("--delete"))
{
    foreach (var entry in AgentCatalog.All)
    {
        await admin.DeleteAgentAsync(entry.Spec.Name, force: true);
        Console.WriteLine($"deleted {entry.Spec.Name}");
    }
    return;
}

var mcpUrl = new Uri(Required("MCP_SERVER_URL"));
var schemaOptions = new AIJsonSchemaCreateOptions
{
    // Strict structured output: every property required, no extra properties. Nullable fields become ["type","null"].
    TransformOptions = new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true, RequireAllProperties = true },
};

foreach (var entry in AgentCatalog.All)
{
    var spec = entry.Spec;

    // The answer format must be part of the agent definition: a schema passed at call time is ignored for registered agents.
    var schema = AIJsonUtilities.CreateJsonSchema(entry.OutputType, serializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web), inferenceOptions: schemaOptions);
    var definition = new DeclarativeAgentDefinition(model)
    {
        Instructions = spec.Instructions,
        TextOptions = new ResponseTextOptions
        {
            TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(entry.OutputType.Name, BinaryData.FromString(schema.GetRawText()), jsonSchemaIsStrict: true),
        },
    };

    // Least privilege: the MCP tool only exposes the tools on this agent's allow-list. Agents without tools get none.
    if (spec.AllowedTools.Count > 0)
    {
        var filter = new McpToolFilter();
        foreach (var tool in spec.AllowedTools) filter.ToolNames.Add(tool);

        var mcp = ResponseTool.CreateMcpTool(
            serverLabel: "leakops_mcp",
            serverUri: mcpUrl,
            allowedTools: filter,
            toolCallApprovalPolicy: new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.NeverRequireApproval));
        mcp.ProjectConnectionId = connection;
        definition.Tools.Add(mcp);
    }

    // The hash of everything that defines the agent decides whether a new version is needed.
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', model, spec.Instructions, schema.GetRawText(), connection, mcpUrl, string.Join(',', spec.AllowedTools))))).ToLowerInvariant()[..16];

    string? currentHash = null;
    (await LatestVersionAsync(spec.Name))?.Metadata.TryGetValue("definitionHash", out currentHash);

    if (currentHash == hash)
    {
        Console.WriteLine($"{spec.Name}: up to date");
        continue;
    }

    var options = new ProjectsAgentVersionCreationOptions(definition) { Description = entry.Description };
    options.Metadata["definitionHash"] = hash;
    options.Metadata["demo"] = "evides-leakops";
    var created = (await admin.CreateAgentVersionAsync(spec.Name, options)).Value;
    Console.WriteLine($"{spec.Name}: created version {created.Version} ({spec.AllowedTools.Count} tool(s))");
}
