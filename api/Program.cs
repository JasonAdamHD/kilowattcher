using Kilowattcher.Api;
using Kilowattcher.Api.Agent;
using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Safety;
using Kilowattcher.Api.Tools;
using Kilowattcher.Api.Weather;

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
builder.Services.Configure<HomeLocation>(builder.Configuration.GetSection(HomeLocation.Section));
builder.Services.AddSingleton<HomeClock>();

// Live Open-Meteo by default; WEATHER_SOURCE=recorded replays a saved forecast (tests and evals).
if (string.Equals(builder.Configuration["WEATHER_SOURCE"], "recorded", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IWeatherSource, RecordedWeatherSource>();
}
else
{
    builder.Services.AddHttpClient<OpenMeteoWeatherSource>(client => client.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<IWeatherSource>(sp => sp.GetRequiredService<OpenMeteoWeatherSource>());
}
builder.Services.AddSingleton<ITool, BatteryStatusTool>();
builder.Services.AddSingleton<ITool, BackupEstimateTool>();
builder.Services.AddSingleton<ITool, RateScheduleTool>();
builder.Services.AddSingleton<ITool, ChargingWindowTool>();
builder.Services.AddSingleton<ITool, SolarForecastTool>();
builder.Services.AddSingleton<DocIndexProvider>();
builder.Services.AddSingleton<IEmbeddingClient, AzureEmbeddingClient>();
builder.Services.AddSingleton<ITool, SearchDocsTool>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<IAnswerJudge, AzureAnswerJudge>();
builder.Services.AddHttpClient<IPromptShield, AzurePromptShield>(client => client.Timeout = TimeSpan.FromSeconds(10));
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
