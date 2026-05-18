using System.Linq;
using AtcMcpServer.Domain;
using AtcMcpServer.State;
using Xunit;
using static AtcMcpServer.Tests.TestHelpers;

namespace AtcMcpServer.Tests;

/// <summary>
/// R8: cancelling a flight marks it cancelled AND causes dependent ops to be
/// re-evaluated. Dependants of a cancelled flight should surface a clear
/// DEPENDENCY_CANCELLED reason on the next schedule generation.
/// </summary>
public class CancellationTests
{
    [Fact]
    public void Cancel_MarksFlightAsCancelled_DependentRevealedAsDepCancelled()
    {
        var state = new AirportState(DefaultConfig());
        state.SubmitFlight(Flight("IB100", OperationType.Arrival,   Priority.Medium));
        state.SubmitFlight(Flight("OB200", OperationType.Departure, Priority.Medium, deps: new[] { "IB100" }));

        // Initial schedule — both are scheduled.
        var first = state.GenerateSchedule();
        Assert.Equal(2, first.Scheduled.Count);

        // Cancel the inbound. Dependant should be flipped back to Queued internally.
        bool ok = state.CancelFlight("IB100");
        Assert.True(ok);
        Assert.Equal(FlightStatus.Cancelled, state.TryGetFlight("IB100")!.Status);
        Assert.Equal(FlightStatus.Queued,    state.TryGetFlight("OB200")!.Status);

        // Re-generate schedule — dependant should now be unscheduled with cancelled-dep reason.
        var second = state.GenerateSchedule();
        Assert.DoesNotContain(second.Scheduled, e => e.FlightNumber == "IB100"); // cancelled, not in schedule
        Assert.DoesNotContain(second.Scheduled, e => e.FlightNumber == "OB200"); // dep cancelled, can't fly
        var orphan = second.Unscheduled.Single(u => u.FlightNumber == "OB200");
        Assert.Equal(UnscheduleReasons.DependencyCancelled, orphan.ReasonCode);
    }

    [Fact]
    public void CancellingNonExistentFlight_ReturnsFalse()
    {
        var state = new AirportState(DefaultConfig());
        Assert.False(state.CancelFlight("ZZ999"));
    }

    [Fact]
    public void CancellingAlreadyCancelledFlight_ReturnsFalse()
    {
        var state = new AirportState(DefaultConfig());
        state.SubmitFlight(Flight("AA001", OperationType.Arrival, Priority.High));
        Assert.True(state.CancelFlight("AA001"));
        Assert.False(state.CancelFlight("AA001"));
    }
}
