using System;
using System.Collections.Generic;

namespace AtcMcpServer.Domain;

/// <summary>
/// A single flight submission. Mutable state lives here; the scheduler reads
/// the queue, then writes back Status / UnscheduleReason / ScheduledEntry.
///
/// FlightNumber is the natural primary key — clients reference flights by it
/// in dependencies and in cancel operations.
///
/// SubmissionOrder is a monotonic counter assigned by AirportState at insert
/// time. It is the deterministic tie-breaker when priority + dependency depth
/// don't fully order two flights, satisfying R11 (deterministic scheduling).
/// </summary>
public sealed class Flight
{
    public required string FlightNumber { get; init; }
    public required OperationType Operation { get; init; }
    public required Priority Priority { get; init; }

    /// <summary>Other flight numbers this flight depends on (must finish first).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Minimum runway length required (meters). Null = any runway works.
    /// Per R5a / scenario 2: if no runway in the airport's pool meets this,
    /// the flight is left Unscheduled with reason NO_SUITABLE_RUNWAY.
    /// </summary>
    public int? MinRunwayLengthMeters { get; init; }

    /// <summary>
    /// Required runway category (e.g. "heavy" for wide-body, "medium" for narrow-body).
    /// Null = any category works. Treated identically to MinRunwayLengthMeters as a
    /// capability gate.
    /// </summary>
    public string? RequiredRunwayCategory { get; init; }

    // === Mutable state filled by AirportState / Scheduler ===

    public int SubmissionOrder { get; set; }
    public FlightStatus Status { get; set; } = FlightStatus.Queued;
    public string? UnscheduleReason { get; set; }
    public string? UnscheduleMessage { get; set; }

    /// <summary>The slot allocated by the last successful scheduler run.</summary>
    public ScheduleEntry? ScheduledEntry { get; set; }
}
