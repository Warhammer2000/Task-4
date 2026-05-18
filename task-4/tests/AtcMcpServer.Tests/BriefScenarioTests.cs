using System.Linq;
using AtcMcpServer.Domain;
using AtcMcpServer.State;
using Xunit;
using static AtcMcpServer.Tests.TestHelpers;

namespace AtcMcpServer.Tests;

/// <summary>
/// The 3 explicit validation scenarios from BRIEF.md. These are the gate for
/// considering the scheduler "done" — every single one must pass at submit time.
/// Each test mirrors the brief's step-by-step instructions and asserts every
/// "Expected result" bullet.
/// </summary>
public class BriefScenarioTests
{
    // ===========================================================================
    // Scenario 1 — Morning Rush: mixed arrivals and departures, priority respected
    // ===========================================================================
    [Fact]
    public void Scenario1_MorningRush_AllSchedulableSchedule_NoOverlaps_HighPriorityEarliest()
    {
        var state = new AirportState(DefaultConfig());

        state.SubmitFlight(Flight("AA001", OperationType.Arrival,   Priority.High));   // high-pri arrival
        state.SubmitFlight(Flight("AA002", OperationType.Departure, Priority.Medium)); // medium-pri departure
        state.SubmitFlight(Flight("AA003", OperationType.Arrival,   Priority.Low));    // low-pri arrival
        state.SubmitFlight(Flight("AA004", OperationType.Departure, Priority.Low));    // low-pri departure

        var result = state.GenerateSchedule();

        // All 4 are schedulable (we have 2 runways, 4 gates, 4 ground crew, plenty).
        Assert.Equal(4, result.Scheduled.Count);
        Assert.Empty(result.Unscheduled);

        // No overlaps on the same runway or gate.
        AssertNoRunwayOrGateOverlaps(result.Scheduled);

        // Higher priority earlier than lowest priority when contested.
        var highArrival = result.Scheduled.Single(e => e.FlightNumber == "AA001");
        var lowArrival  = result.Scheduled.Single(e => e.FlightNumber == "AA003");
        Assert.True(highArrival.StartUtc <= lowArrival.StartUtc,
            "High-priority arrival should not start later than the low-priority arrival.");
    }

    // ===========================================================================
    // Scenario 2 — Heavy Hauler: runway capability mismatch is reported clearly
    // ===========================================================================
    [Fact]
    public void Scenario2_HeavyHauler_OversizedDepartureUnscheduledWithReason_OthersStillFit()
    {
        // Two runways, neither long enough for the heavy hauler.
        var cfg = DefaultConfig(runways: new[]
        {
            new Runway("R1", 2500, "short"),
            new Runway("R2", 3000, "medium"),
        });
        var state = new AirportState(cfg);

        state.SubmitFlight(Flight("HX900", OperationType.Departure, Priority.High, minRunwayLen: 4500));
        state.SubmitFlight(Flight("AA100", OperationType.Arrival,   Priority.Medium));

        var result = state.GenerateSchedule();

        // Oversized flight must NOT be scheduled.
        Assert.DoesNotContain(result.Scheduled, e => e.FlightNumber == "HX900");

        // ...but must be present in the unscheduled list with a clear reason.
        var heavy = result.Unscheduled.Single(u => u.FlightNumber == "HX900");
        Assert.Equal(UnscheduleReasons.NoSuitableRunway, heavy.ReasonCode);
        Assert.Contains("runway", heavy.ReasonMessage, System.StringComparison.OrdinalIgnoreCase);

        // The other flight should still schedule fine.
        Assert.Contains(result.Scheduled, e => e.FlightNumber == "AA100");
    }

    // ===========================================================================
    // Scenario 3 — Connecting Flight: outbound waits for inbound + dep buffer
    // ===========================================================================
    [Fact]
    public void Scenario3_ConnectingFlight_OutboundWaitsForInboundPlusBuffer()
    {
        var cfg = DefaultConfig();
        var state = new AirportState(cfg);

        state.SubmitFlight(Flight("IB100", OperationType.Arrival,   Priority.Medium));
        state.SubmitFlight(Flight("OB200", OperationType.Departure, Priority.Medium, deps: new[] { "IB100" }));

        var result = state.GenerateSchedule();

        Assert.Equal(2, result.Scheduled.Count);
        Assert.Empty(result.Unscheduled);

        var inbound  = result.Scheduled.Single(e => e.FlightNumber == "IB100");
        var outbound = result.Scheduled.Single(e => e.FlightNumber == "OB200");

        // Outbound must START no earlier than (inbound.End + dependency_buffer).
        var minOutboundStart = inbound.EndUtc.AddSeconds(cfg.DependencyBufferSeconds);
        Assert.True(outbound.StartUtc >= minOutboundStart,
            $"Outbound start {outbound.StartUtc:O} must be >= inbound end + buffer {minOutboundStart:O}");

        // Timeline order: inbound starts before outbound (the "dependency order" the brief asks for).
        Assert.True(inbound.StartUtc < outbound.StartUtc);
    }

    // ===========================================================================
    // Cross-cutting helper
    // ===========================================================================
    private static void AssertNoRunwayOrGateOverlaps(System.Collections.Generic.IReadOnlyList<ScheduleEntry> scheduled)
    {
        for (int i = 0; i < scheduled.Count; i++)
        {
            for (int j = i + 1; j < scheduled.Count; j++)
            {
                var a = scheduled[i]; var b = scheduled[j];
                bool overlap(System.DateTime aStart, System.DateTime aEnd, System.DateTime bStart, System.DateTime bEnd)
                    => aStart < bEnd && bStart < aEnd;
                if (a.RunwayId == b.RunwayId)
                    Assert.False(overlap(a.StartUtc, a.EndUtc, b.StartUtc, b.EndUtc),
                        $"Runway {a.RunwayId} double-booked: {a.FlightNumber} and {b.FlightNumber}");
                if (a.GateId == b.GateId)
                    Assert.False(overlap(a.StartUtc, a.EndUtc, b.StartUtc, b.EndUtc),
                        $"Gate {a.GateId} double-booked: {a.FlightNumber} and {b.FlightNumber}");
            }
        }
    }
}
