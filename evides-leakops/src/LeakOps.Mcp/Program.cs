using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

// MCP tool functions (SCADA, GIS, workforce, inventory ...) are added in step 3.
// C# note: the Functions "isolated worker" runs your code in its own process, started like any .NET host.
var builder = FunctionsApplication.CreateBuilder(args);

builder.Build().Run();
