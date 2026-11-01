using System.Text.Json;

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

/// <summary>Mock home battery. Fixed values so evals stay repeatable.</summary>
public sealed class BatteryStatusTool : ITool
{
    public static readonly BatteryStatus Status = new(
        Product: "VoltNest 13",
        CapacityKwh: 13.5,
        StateOfChargePercent: 42,
        StoredKwh: 5.67,
        BackupReservePercent: 20,
        MaxChargeKw: 5.0,
        MaxDischargeKw: 5.0,
        Mode: "discharging",
        PowerKw: 1.2);

    public string Name => "get_battery_status";

    public string Description =>
        "Current state of the home battery: capacity, state of charge, stored energy, backup reserve, " +
        "charge and discharge limits, and whether it is charging, discharging, or idle right now.";

    public string ParametersSchema => ToolArguments.NoArgumentsSchema;

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult<object>(Status);

    internal object Execute(JsonElement arguments) => Status;
}
