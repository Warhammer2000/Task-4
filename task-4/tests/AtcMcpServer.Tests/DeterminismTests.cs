using System.Linq;
using AtcMcpServer.Domain;
using AtcMcpServer.State;
using Xunit;
using static AtcMcpServer.Tests.TestHelpers;

namespace AtcMcpServer.Tests;

/// <summary>
/// R11: "Repeated scheduling with the same inputs and configuration should produce
/// deterministic results." Two independent AirportState instances seeded identically
/// must produce schedules with identical sequences of (flight, runway, gate, start, end).
/// </summary>
public class DeterminismTests
{
    [Fact]
    public void SameInputs_TwoRuns_ProduceIdenticalSchedules()
    {
        var resA = ScheduleOnce();
        var resB = ScheduleOnce();

        Assert.Equal(resA.Scheduled.Count, resB.Scheduled.Count);
        for (int i = 0; i < resA.Scheduled.Count; i++)
        {
            var a = resA.Scheduled[i]; var b = resB.Scheduled[i];
            Assert.Equal(a.FlightNumber, b.FlightNumber);
            Assert.Equal(a.RunwayId, b.RunwayId);
            Assert.Equal(a.GateId, b.GateId);
            Assert.Equal(a.StartUtc, b.StartUtc);
            Assert.Equal(a.EndUtc, b.EndUtc);
        }
        Assert.Equal(resA.Unscheduled.Count, resB.Unscheduled.Count);
        for (int i = 0; i < resA.Unscheduled.Count; i++)
        {
            Assert.Equal(resA.Unscheduled[i].FlightNumber, resB.Unscheduled[i].FlightNumber);
            Assert.Equal(resA.Unscheduled[i].ReasonCode, resB.Unscheduled[i].ReasonCode);
        }
    }

    [Fact]
    public void GenerateScheduleTwice_OnSameState_ProducesIdenticalSchedule()
    {
        var state = new AirportState(DefaultConfig());
        state.SubmitFlight(Flight("AA001", OperationType.Arrival, Priority.High));
        state.SubmitFlight(Flight("AA002", OperationType.Departure, Priority.Medium));
        state.SubmitFlight(Flight("AA003", OperationType.Arrival, Priority.Low));

        var r1 = state.GenerateSchedule();
        var r2 = state.GenerateSchedule();

        Assert.Equal(r1.Scheduled.Count, r2.Scheduled.Count);
        for (int i = 0; i < r1.Scheduled.Count; i++)
        {
            Assert.Equal(r1.Scheduled[i], r2.Scheduled[i]);
        }
    }

    private static Domain.ScheduleResult ScheduleOnce()
    {
        var state = new AirportState(DefaultConfig());
        state.SubmitFlight(Flight("BA101", OperationType.Departure, Priority.High));
        state.SubmitFlight(Flight("LH202", OperationType.Arrival,   Priority.High));
        state.SubmitFlight(Flight("AF303", OperationType.Departure, Priority.Medium));
        state.SubmitFlight(Flight("DL404", OperationType.Arrival,   Priority.Low));
        state.SubmitFlight(Flight("EK505", OperationType.Departure, Priority.Low, deps: new[] { "LH202" }));
        return state.GenerateSchedule();
    }
}
