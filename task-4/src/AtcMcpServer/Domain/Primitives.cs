namespace AtcMcpServer.Domain;

/// <summary>Operation type for a submitted flight.</summary>
public enum OperationType
{
    Arrival,
    Departure
}

/// <summary>
/// Flight priority. Integer values are sorted ascending — High=0 is scheduled earliest
/// when resources are contested (per R6 of the brief).
/// </summary>
public enum Priority
{
    High = 0,
    Medium = 1,
    Low = 2
}

/// <summary>
/// Lifecycle state of a submitted flight.
///   Queued       — submitted, not yet considered by a scheduler run.
///   Scheduled    — has a slot on a runway + gate within the schedule horizon.
///   Unscheduled  — last scheduler run could not place this flight; <see cref="Flight.UnscheduleReason"/>
///                  carries a machine-readable reason code.
///   Cancelled    — user invoked the cancel tool on it; dependents are re-evaluated on next schedule.
/// </summary>
public enum FlightStatus
{
    Queued,
    Scheduled,
    Unscheduled,
    Cancelled
}

/// <summary>
/// Machine-readable reason codes for why a flight ended up Unscheduled.
/// Stable values intended to be parsed by clients; human-readable message in
/// <see cref="Flight.UnscheduleMessage"/>.
/// </summary>
public static class UnscheduleReasons
{
    public const string NoSuitableRunway = "NO_SUITABLE_RUNWAY";
    public const string DependencyMissing = "DEPENDENCY_MISSING";
    public const string DependencyCancelled = "DEPENDENCY_CANCELLED";
    public const string DependencyUnscheduled = "DEPENDENCY_UNSCHEDULED";
    public const string DependencyCycle = "DEPENDENCY_CYCLE";
    public const string HorizonExceeded = "HORIZON_EXCEEDED";
    public const string NoResourceSlotInHorizon = "NO_RESOURCE_SLOT_IN_HORIZON";
}
