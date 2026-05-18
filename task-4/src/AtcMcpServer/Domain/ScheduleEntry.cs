using System;

namespace AtcMcpServer.Domain;

/// <summary>
/// A single slot in the generated schedule: this flight uses this runway and gate
/// during [Start, End). Times are UTC; the scheduler uses logical seconds since
/// scheduling start internally (see Scheduler.cs) but emits UTC at the boundary.
/// </summary>
public sealed record ScheduleEntry(
    string FlightNumber,
    OperationType Operation,
    Priority Priority,
    string RunwayId,
    string GateId,
    DateTime StartUtc,
    DateTime EndUtc);
