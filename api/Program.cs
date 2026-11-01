using Kilowattcher.Api;
using Kilowattcher.Api.Agent;
using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tools;

var builder = WebApplication.CreateBuilder(args);

// Vercel sends traffic to the port in PORT. The chiseled image runs as non-root,
// so 8080 is the safe default (non-root can't bind port 80).
var port = builder.Configuration["PORT"] ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Every setting comes through IConfiguration: user-secrets locally, environment variables on Vercel.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InviteCodeVerifier>();
builder.Services.AddSingleton<SessionTokenService>();
builder.Services.AddSingleton<HistorySigner>();
builder.Services.AddSingleton<IChatModel, AzureOpenAIChatModel>();
builder.Services.AddSingleton<HomeClock>();
builder.Services.AddSingleton<ITool, BatteryStatusTool>();
builder.Services.AddSingleton<ITool, RateScheduleTool>();
builder.Services.AddSingleton<ITool, SolarForecastTool>();
builder.Services.AddSingleton<DocIndexProvider>();
builder.Services.AddSingleton<IEmbeddingClient, AzureEmbeddingClient>();
builder.Services.AddSingleton<ITool, SearchDocsTool>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddKilowattcherRateLimits(); // needs TimeProvider, registered above

var app = builder.Build();

app.UseRateLimiter();

// Vercel passes the full path to the container, so every route keeps the /api prefix.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapChatEndpoints();

app.Run();

// Lets the test project boot the app in memory with WebApplicationFactory.
public partial class Program;
