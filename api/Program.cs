var builder = WebApplication.CreateBuilder(args);

// Vercel sends traffic to the port in PORT. The chiseled image runs as non-root,
// so 8080 is the safe default (non-root can't bind port 80).
var port = builder.Configuration["PORT"] ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// Vercel passes the full path to the container, so every route keeps the /api prefix.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Lets the test project boot the app in memory with WebApplicationFactory.
public partial class Program;
