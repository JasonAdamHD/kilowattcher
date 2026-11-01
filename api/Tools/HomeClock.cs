namespace Kilowattcher.Api.Tools;

/// <summary>
/// Local time at the fictional home in Phoenix, Arizona. Arizona doesn't observe daylight saving,
/// so it is UTC-7 all year. A fixed offset also means no time-zone database is needed, which the
/// chiseled runtime image doesn't ship.
/// </summary>
public sealed class HomeClock(TimeProvider time)
{
    public const string TimeZoneLabel = "America/Phoenix (UTC-7, no daylight saving)";
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-7);

    public DateTimeOffset Now => time.GetUtcNow().ToOffset(Offset);

    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
}
