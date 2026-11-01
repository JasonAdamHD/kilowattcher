namespace Kilowattcher.Api;

/// <summary>Machine-readable error code; the web app maps each code to friendly wording.</summary>
public sealed record ApiError(string Error);

public static class ApiErrors
{
    public static IResult Unauthorized() =>
        Results.Json(new ApiError("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);

    public static IResult NotConfigured() =>
        Results.Json(new ApiError("not_configured"), statusCode: StatusCodes.Status503ServiceUnavailable);
}
