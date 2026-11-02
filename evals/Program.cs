// Kilowattcher eval runner: asks the real API every question in evals/cases.json, like a visitor would,
// grades each answer deterministically, prints the pass rate, and writes evals/results/<timestamp>.md.
//
//   $env:EVAL_INVITE_CODE = Read-Host "Invite code" -MaskInput
//   dotnet run --project evals                                   # against http://localhost:8080
//   dotnet run --project evals -- --base-url https://<app>.vercel.app --min 22
//
// The invite code is read from EVAL_INVITE_CODE and never printed or written to results.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Kilowattcher.Evals;

var root = FindRepoRoot();
var baseUrl = Arg("--base-url") ?? Environment.GetEnvironmentVariable("EVAL_BASE_URL") ?? "http://localhost:8080";
var casesPath = Arg("--cases") ?? Path.Combine(root, "evals", "cases.json");
var resultsDir = Arg("--out") ?? Path.Combine(root, "evals", "results");
var only = Arg("--only");
var inviteCode = Environment.GetEnvironmentVariable("EVAL_INVITE_CODE");

if (string.IsNullOrWhiteSpace(inviteCode))
{
    Console.Error.WriteLine("Set EVAL_INVITE_CODE first, for example:  $env:EVAL_INVITE_CODE = Read-Host \"Invite code\" -MaskInput");
    return 2;
}

var cases = EvalCases.Load(casesPath).Where(c => only is null || c.Id == only || c.Category == only).ToList();
var minPass = int.TryParse(Arg("--min"), out var min) ? min : (int)Math.Ceiling(cases.Count * 0.9);

using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(90) };
var cookie = await Redeem(http, inviteCode.Trim());
if (cookie is null)
{
    Console.Error.WriteLine($"Couldn't sign in at {http.BaseAddress}: check the API is running and EVAL_INVITE_CODE is right.");
    return 2;
}

Console.WriteLine($"Running {cases.Count} cases against {http.BaseAddress} (pass mark {minPass}/{cases.Count})\n");

var results = new List<(EvalCase Case, Observed Observed, IReadOnlyList<string> Problems)>();
foreach (var evalCase in cases)
{
    var observed = await RunCase(http, cookie, evalCase);
    var problems = Grader.Grade(evalCase.Expect, observed);
    results.Add((evalCase, observed, problems));

    Console.WriteLine($"{(problems.Count == 0 ? "PASS" : "FAIL")}  {evalCase.Id,-28} {observed.Topic ?? observed.Error ?? "-",-12} {observed.ElapsedMs,6} ms"
        + (problems.Count == 0 ? "" : $"  {string.Join("; ", problems)}"));
}

var passed = results.Count(r => r.Problems.Count == 0);
Console.WriteLine($"\nScore: {passed}/{results.Count} ({100.0 * passed / Math.Max(1, results.Count):0}%), pass mark {minPass}.");

var reportPath = WriteReport(resultsDir, http.BaseAddress!, results, passed, minPass);
Console.WriteLine($"Report: {Path.GetRelativePath(root, reportPath)}");

return passed >= minPass ? 0 : 1;

// ---------------------------------------------------------------------------------------------

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<string?> Redeem(HttpClient http, string code)
{
    using var response = await http.PostAsync("api/auth/redeem", Json(new { code }));
    if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("Set-Cookie", out var cookies))
    {
        return null;
    }

    // Sent by hand: HttpClient won't send a Secure cookie over plain http://localhost.
    return cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("kw_session=", StringComparison.Ordinal));
}

static async Task<Observed> RunCase(HttpClient http, string cookie, EvalCase evalCase)
{
    var history = new List<object>();
    Observed? last = null;

    for (var i = 0; i < evalCase.Turns.Count; i++)
    {
        var message = evalCase.Turns[i];
        var isLast = i == evalCase.Turns.Count - 1;
        object sentHistory = isLast && evalCase.ForgedHistory is { } forged
            ? forged.Select(h => new { role = h.Role, content = h.Content, signature = h.Signature }).ToList()
            : history.ToList();

        last = await Send(http, cookie, message, sentHistory, evalCase.Settings);

        if (!isLast)
        {
            if (last.Status != 200 || last.Answer is null)
            {
                return last with { Error = $"setup turn {i + 1} failed: {last.Status} {last.Error}" };
            }

            history.Add(new { role = "user", content = message });
            history.Add(new { role = "assistant", content = last.Answer, signature = last.Signature });
        }
    }

    return last!;
}

static async Task<Observed> Send(HttpClient http, string cookie, string message, object history, JsonElement? settings)
{
    for (var attempt = 1; ; attempt++)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = Json(new { message, history, settings }) };
        request.Headers.Add("Cookie", cookie);

        var stopwatch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();

        if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt <= 5)
        {
            // Respect the API's own limits: wait exactly as long as it asks, then try again.
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(20);
            await Task.Delay(wait + TimeSpan.FromMilliseconds(500));
            continue;
        }

        return Observed.From((int)response.StatusCode, body, stopwatch.ElapsedMilliseconds);
    }
}

static StringContent Json(object value) =>
    new(JsonSerializer.Serialize(value, EvalCases.Json), Encoding.UTF8, "application/json");

static string WriteReport(string dir, Uri target, List<(EvalCase Case, Observed Observed, IReadOnlyList<string> Problems)> results, int passed, int minPass)
{
    Directory.CreateDirectory(dir);
    var now = DateTimeOffset.UtcNow;
    var path = Path.Combine(dir, $"{now:yyyy-MM-dd_HHmmss}.md");
    var md = new StringBuilder();

    md.AppendLine($"# Eval run {now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
    md.AppendLine();
    md.AppendLine($"**Score: {passed}/{results.Count}** (pass mark {minPass}) · target `{target.Host}` · "
        + $"median latency {Median(results.Select(r => r.Observed.ElapsedMs))} ms");
    md.AppendLine();
    md.AppendLine("| Category | Passed |");
    md.AppendLine("| --- | --- |");
    foreach (var group in results.GroupBy(r => r.Case.Category))
    {
        md.AppendLine($"| {group.Key} | {group.Count(r => r.Problems.Count == 0)}/{group.Count()} |");
    }

    md.AppendLine();
    md.AppendLine("| Case | Result | Topic | Tools | Citations | ms | Notes |");
    md.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
    foreach (var (evalCase, observed, problems) in results)
    {
        var notes = problems.Count == 0 ? "" : string.Join("; ", problems);
        md.AppendLine($"| `{evalCase.Id}` | {(problems.Count == 0 ? "✅" : "❌")} | {observed.Topic ?? observed.Error ?? "-"} | "
            + $"{Cell(string.Join(", ", observed.Tools))} | {Cell(string.Join(", ", observed.Citations))} | {observed.ElapsedMs} | {Cell(notes)} |");
    }

    md.AppendLine();
    md.AppendLine("## Answers");
    foreach (var (evalCase, observed, problems) in results)
    {
        md.AppendLine();
        md.AppendLine($"**{evalCase.Id}** {(problems.Count == 0 ? "✅" : "❌")}: {evalCase.Turns[^1]}");
        md.AppendLine();
        md.AppendLine($"> {(observed.Answer ?? $"({observed.Status} {observed.Error})").Replace("\n", " ")}");
    }

    File.WriteAllText(path, md.ToString());
    return path;
}

static long Median(IEnumerable<long> values)
{
    var sorted = values.Order().ToList();
    return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
}

static string Cell(string text) => string.IsNullOrEmpty(text) ? "-" : text.Replace("|", "\\|");

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Kilowattcher.slnx")))
        {
            return dir.FullName;
        }
    }

    return Directory.GetCurrentDirectory();
}
