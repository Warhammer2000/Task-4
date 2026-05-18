using System;
using System.Collections.Generic;
using AtcMcpServer.Configuration;
using AtcMcpServer.Domain;

namespace AtcMcpServer.Tests;

/// <summary>
/// Builders for tests. Everything pins SchedulingStartUtc to a fixed timestamp so
/// schedule outputs are byte-identical across runs — the R11 determinism invariant
/// becomes trivially testable.
/// </summary>
internal static class TestHelpers
{
    /// <summary>2030-01-15 06:00:00 UTC — arbitrary fixed instant for deterministic test output.</summary>
    public static readonly DateTime FixedStart = new(2030, 1, 15, 6, 0, 0, DateTimeKind.Utc);

    public static AirportConfig DefaultConfig(
        IReadOnlyList<Runway>? runways = null,
        int gateCount = 4,
        int groundCrew = 4,
        int sepTakeoff = 60,
        int sepLanding = 90,
        int sepMixed = 120,
        int turnaround = 600,
        int depBuffer = 300,
        int opArrival = 600,
        int opDeparture = 480,
        int horizonHours = 12,
        DateTime? start = null)
    {
        return new AirportConfig
        {
            Runways = runways ?? new[]
            {
                new Runway("R1", 3500, "medium"),
                new Runway("R2", 4000, "heavy"),
            },
            GateCount = gateCount,
            GroundCrewCount = groundCrew,
            SeparationBufferTakeoffSeconds = sepTakeoff,
            SeparationBufferLandingSeconds = sepLanding,
            SeparationBufferMixedSeconds = sepMixed,
            GateTurnaroundSeconds = turnaround,
            DependencyBufferSeconds = depBuffer,
            OperationDurationArrivalSeconds = opArrival,
            OperationDurationDepartureSeconds = opDeparture,
            SchedulingHorizonHours = horizonHours,
            SchedulingStartUtc = start ?? FixedStart,
        };
    }

    public static Flight Flight(
        string number,
        OperationType op,
        Priority priority,
        IReadOnlyList<string>? deps = null,
        int? minRunwayLen = null,
        string? requiredCategory = null)
    {
        return new Flight
        {
            FlightNumber = number,
            Operation = op,
            Priority = priority,
            DependsOn = deps ?? Array.Empty<string>(),
            MinRunwayLengthMeters = minRunwayLen,
            RequiredRunwayCategory = requiredCategory,
        };
    }
}
