using System.Text.Json;

namespace Kilowattcher.Api.Tools;

public sealed record BackupEstimate(
    int StateOfChargePercent,
    int BackupFloorPercent,
    double UsableKwh,
    double LoadKw,
    string LoadSource,
    double EstimatedHours,
    string Formula);

/// <summary>
/// Works out how long the battery can power the home in an outage, in code, so the answer is the same
/// every time. The model only explains the result. The formula and the 5% floor come from
/// api/Data/docs/outage-backup.md ("How long backup lasts"); a test keeps the two in step.
/// </summary>
public sealed class BackupEstimateTool : ITool
{
    /// <summary>The battery always keeps this much to protect itself, even during an outage.</summary>
    public const int BackupFloorPercent = 5;

    public string Name => "estimate_backup_time";

    public string Description =>
        "Estimate how many hours the home battery can power the house if the grid goes down, using the live " +
        "state of charge and the documented backup formula (usable energy above the 5% floor divided by the home's load). " +
        "Pass load_kw to estimate for a different load, or null to use the home's current load.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "load_kw": {
              "type": ["number", "null"],
              "description": "Home load in kW to estimate for, or null for the current load."
            }
          },
          "required": ["load_kw"],
          "additionalProperties": false
        }
        """;

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments));

    internal object Execute(JsonElement arguments) => Estimate(BatteryStatusTool.Status, ReadLoad(arguments));

    internal static object Estimate(BatteryStatus battery, double? requestedLoadKw)
    {
        double loadKw;
        string loadSource;

        if (requestedLoadKw is { } requested)
        {
            if (requested <= 0 || double.IsNaN(requested))
            {
                return new ToolError("load_kw must be greater than 0.");
            }

            if (requested > battery.MaxDischargeKw)
            {
                return new ToolError($"The battery can supply at most {battery.MaxDischargeKw} kW; a {requested} kW load is more than backup can power.");
            }

            loadKw = requested;
            loadSource = "requested";
        }
        else if (battery.Mode == "discharging" && battery.PowerKw > 0)
        {
            loadKw = battery.PowerKw;
            loadSource = "current home load (battery discharge rate)";
        }
        else
        {
            return new ToolError("The battery isn't powering the home right now, so there's no current load. Pass load_kw.");
        }

        var usablePercent = Math.Max(0, battery.StateOfChargePercent - BackupFloorPercent);
        var usableKwh = battery.CapacityKwh * usablePercent / 100.0;

        return new BackupEstimate(
            battery.StateOfChargePercent,
            BackupFloorPercent,
            Math.Round(usableKwh, 2),
            loadKw,
            loadSource,
            Math.Round(usableKwh / loadKw, 1),
            $"({battery.StateOfChargePercent}% - {BackupFloorPercent}%) x {battery.CapacityKwh} kWh / {loadKw} kW");
    }

    private static double? ReadLoad(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty("load_kw", out var load)
        && load.ValueKind == JsonValueKind.Number
            ? load.GetDouble()
            : null;
}
