using System.Reflection;
using Kilowattcher.Api.Tools;
using Kilowattcher.Api.Weather;

namespace Kilowattcher.Api.Tests.TestSupport;

/// <summary>The recorded Open-Meteo forecast from the repo, replayed with its first day as "tomorrow".</summary>
public static class TestWeather
{
    public static string RecordedPath
    {
        get
        {
            var root = typeof(TestWeather).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
            return Path.Combine(root, "api", "Data", "weather", RecordedWeatherSource.FileName);
        }
    }

    public static RecordedWeatherSource Recorded() => new(RecordedPath);
}
