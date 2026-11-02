using System.Text.Json;
using Kilowattcher.Api.Households;

namespace Kilowattcher.Api.Tools;

public sealed record BatteryStatus(
    string Product,
    double CapacityKwh,
    int StateOfChargePercent,
    double StoredKwh,
    int BackupReservePercent,
    double MaxChargeKw,
    double MaxDischargeKw,
    string Mode,
    double PowerKw);

/// <summary>The household's battery right now: its model's specs plus the charge level and load for this request.</summary>
public sealed class BatteryStatusTool : ITool
{
    public string Name => "get_battery_status";

    public string Description =>
        "Current state of the home battery: model, capacity, state of charge, stored energy, backup reserve, " +
        "charge and discharge limits, and whether it is charging, discharging, or idle right now.";

    public string ParametersSchema => ToolArguments.NoArgumentsSchema;

    public Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<object>(context.Household.Status);
}
