// Fake Waterstoring.nl. The incident ingest API and the public map page are added in step 2.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "waterstoring" }));

app.Run();
