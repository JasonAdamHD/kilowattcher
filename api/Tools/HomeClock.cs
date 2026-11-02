using Microsoft.Extensions.Options;

namespace Kilowattcher.Api.Tools;

/// <summary>Where the fictional home is. Set in appsettings.json ("Home") or with Home__* environment variables.</summary>
public sealed class HomeLocation
{
    public const string Section = "Home";

    public string City { get; set; } = "Phoenix, AZ";
    public double Latitude { get; set; } = 33.4484;
    public double Longitude { get; set; } = -112.0740;

    /// <summary>IANA time zone, for example "America/Phoenix" or "America/New_York".</summary>
    public string TimeZone { get; set; } = "America/Phoenix";
}

/// <summary>
/// Local time at the home, with real time-zone rules (daylight saving included). Needs time-zone data in
/// the container, which is why the API image is the "chiseled-extra" variant.
/// </summary>
public sealed class HomeClock
{
    private readonly TimeProvider _time;

    public HomeClock(TimeProvider time, IOptions<HomeLocation> location)
    {
        _time = time;
        Location = location.Value;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(Location.TimeZone);
    }

    public HomeLocation Location { get; }

    public TimeZoneInfo Zone { get; }

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_time.GetUtcNow(), Zone);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    /// <summary>"Phoenix, AZ (America/Phoenix, UTC-7)", for the system prompt and tool results.</summary>
    public string Label
    {
        get
        {
            var offset = Zone.GetUtcOffset(_time.GetUtcNow());
            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var hours = offset.Duration();
            var utc = hours.Minutes == 0 ? $"UTC{sign}{hours.Hours}" : $"UTC{sign}{hours.Hours}:{hours.Minutes:00}";
            return $"{Location.City} ({Location.TimeZone}, {utc})";
        }
    }

    /// <summary>The local time at a given date and hour, honoring daylight saving.</summary>
    public DateTimeOffset At(DateOnly date, int hour, int minute = 0)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    /// <summary>For tests: a clock at a fixed instant in a given zone.</summary>
    internal static HomeClock For(TimeProvider time, string timeZone = "America/Phoenix") =>
        new(time, Options.Create(new HomeLocation { TimeZone = timeZone }));
}
