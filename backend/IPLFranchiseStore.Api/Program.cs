var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
