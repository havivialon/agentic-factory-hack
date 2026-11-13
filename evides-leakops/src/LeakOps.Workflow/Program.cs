// Workflow host. The pipeline, SSE endpoint and trigger page are added in step 10.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "workflow" }));

app.Run();
