// Local orchestration. The Functions apps (MCP + Agents, started with `func start`) are wired in step 10.
var builder = DistributedApplication.CreateBuilder(args);

var waterstoring = builder.AddProject<Projects.LeakOps_Waterstoring>("waterstoring");

builder.AddProject<Projects.LeakOps_Workflow>("workflow")
    .WithReference(waterstoring);

builder.Build().Run();
