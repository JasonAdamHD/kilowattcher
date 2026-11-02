using System.Globalization;
using System.Text.Json;
using Kilowattcher.Api.Households;

namespace Kilowattcher.Api.Tools;

/// <summary>A function the model can call. Results are plain data, serialized to JSON.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the arguments, in OpenAI strict mode (every property required, no extras).</summary>
    string ParametersSchema { get; }

    /// <param name="context">This request's household (battery, charge, prices, city) and its local clock.</param>
    Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken);
}

public sealed record ToolError(string Error);

public static class ToolArguments
{
    public const string DateOnlySchema = """
        {
          "type": "object",
          "properties": {
            "date": { "type": "string", "description": "Local date at the home, formatted YYYY-MM-DD." }
          },
          "required": ["date"],
          "additionalProperties": false
        }
        """;

    public const string NoArgumentsSchema = """
        { "type": "object", "properties": {}, "required": [], "additionalProperties": false }
        """;

    public static bool TryGetDate(JsonElement arguments, out DateOnly date)
    {
        date = default;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("date", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string? GetString(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class ToolRegistry(IEnumerable<ITool> tools)
{
    private readonly Dictionary<string, ITool> _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<ITool> All => _byName.Values;

    public bool TryGet(string name, out ITool tool) => _byName.TryGetValue(name, out tool!);
}
