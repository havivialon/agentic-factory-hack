using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

// The six agent HTTP functions (Triage, Diagnostic, Communications, Matchmaker, Procurement, Scheduling)
// and their shared agent factory are added in steps 4-9.
var builder = FunctionsApplication.CreateBuilder(args);

builder.Build().Run();
