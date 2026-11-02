using System.Text.RegularExpressions;
using Kilowattcher.Api.Auth;

namespace Kilowattcher.Api.Locations;

public sealed record LocationSearchResponse(IReadOnlyList<SignedPlace> Results);

public static partial class LocationEndpoints
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 60;

    /// <summary>GET /api/locations/search?q=Austin or ?q=78701: up to 5 signed US places for the settings panel.</summary>
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/locations/search", async (string? q, HttpContext http, SessionTokenService sessions,
            ILocationSearch search, LocationSigner signer, CancellationToken cancellationToken) =>
        {
            if (!http.HasValidSession(sessions))
            {
                return ApiErrors.Unauthorized();
            }

            var query = q?.Trim() ?? "";
            if (query.Length is < MinQueryLength or > MaxQueryLength || !Allowed().IsMatch(query))
            {
                return Results.BadRequest(new ApiError("invalid_query"));
            }

            try
            {
                var places = await search.SearchAsync(query, cancellationToken);
                return Results.Ok(new LocationSearchResponse(places.Select(signer.Sign).ToList()));
            }
            catch (LocationSearchUnavailableException)
            {
                return Results.Json(new ApiError("location_search_unavailable"), statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting(RateLimits.LocationSearchPolicy);
    }

    /// <summary>City names and ZIP codes: letters, digits, spaces and a little punctuation.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N} ,.'\-]+$")]
    private static partial Regex Allowed();
}
