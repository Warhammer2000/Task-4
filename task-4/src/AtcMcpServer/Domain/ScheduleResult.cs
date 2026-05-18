using System;
using System.Collections.Generic;

namespace AtcMcpServer.Domain;

/// <summary>
/// Output of one scheduler run. Contains exactly the flights placed (with their slot)
/// plus the flights left unscheduled with a structured reason (per R7).
/// </summary>
public sealed record ScheduleResult(
    IReadOnlyList<ScheduleEntry> Scheduled,
    IReadOnlyList<UnscheduledEntry> Unscheduled,
    DateTime ScheduleGeneratedAtUtc)
{
    /// <summary>UTC time of the last scheduled flight's End, or null if nothing scheduled. R9g.</summary>
    public DateTime? CompletionTimeUtc
    {
        get
        {
            DateTime? max = null;
            foreach (var e in Scheduled)
            {
                if (max is null || e.EndUtc > max) max = e.EndUtc;
            }
            return max;
        }
    }
}

/// <summary>
/// A single flight that the scheduler could not place. ReasonCode is from
/// <see cref="UnscheduleReasons"/>; ReasonMessage is the human-readable detail.
/// </summary>
public sealed record UnscheduledEntry(
    string FlightNumber,
    OperationType Operation,
    Priority Priority,
    string ReasonCode,
    string ReasonMessage);
