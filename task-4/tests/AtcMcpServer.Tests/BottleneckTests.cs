using AtcMcpServer.Domain;
using AtcMcpServer.Scheduling;
using AtcMcpServer.State;
using Xunit;
using static AtcMcpServer.Tests.TestHelpers;

namespace AtcMcpServer.Tests;

/// <summary>
/// R10: bottleneck = longest active scheduled dependency chain. Tests verify the
/// chain ordering, the total duration accounting (ops + buffers), and the
/// Mermaid rendering.
/// </summary>
public class BottleneckTests
{
    [Fact]
    public void Bottleneck_TwoNodeChain_IncludesOperationDurationsAndDependencyBuffer()
    {
        // R10c + R10d combined: chain length = arrival_duration + dep_buffer + departure_duration
        var cfg = DefaultConfig(opArrival: 600, opDeparture: 480, depBuffer: 300);
        var state = new AirportState(cfg);
        state.SubmitFlight(Flight("IB100", OperationType.Arrival,   Priority.Medium));
        state.SubmitFlight(Flight("OB200", OperationType.Departure, Priority.Medium, deps: new[] { "IB100" }));
        var sched = state.GenerateSchedule();

        var analyzer = new BottleneckAnalyzer(cfg);
        var result = analyzer.Analyze(state.AllFlights(), sched);

        Assert.Equal(2, result.Chain.Count);
        Assert.Equal("IB100", result.Chain[0].FlightNumber);
        Assert.Equal("OB200", result.Chain[1].FlightNumber);
        // 600 (arrival) + 300 (buffer) + 480 (departure) = 1380
        Assert.Equal(600 + 300 + 480, result.TotalElapsedSeconds);
        Assert.NotNull(result.MermaidGantt);
        Assert.Contains("gantt", result.MermaidGantt);
    }

    [Fact]
    public void Bottleneck_NoDependencies_ReturnsSingleLongestFlight()
    {
        var state = new AirportState(DefaultConfig());
        state.SubmitFlight(Flight("AA001", OperationType.Arrival,   Priority.High));   // 600s
        state.SubmitFlight(Flight("AA002", OperationType.Departure, Priority.High));   // 480s
        var sched = state.GenerateSchedule();

        var analyzer = new BottleneckAnalyzer(DefaultConfig());
        var result = analyzer.Analyze(state.AllFlights(), sched);

        // The longer single op (arrival, 600s) wins.
        Assert.Single(result.Chain);
        Assert.Equal("AA001", result.Chain[0].FlightNumber);
        Assert.Equal(600, result.TotalElapsedSeconds);
    }

    [Fact]
    public void Bottleneck_EmptySchedule_ReturnsEmptyChain()
    {
        var state = new AirportState(DefaultConfig());
        var sched = state.GenerateSchedule();

        var analyzer = new BottleneckAnalyzer(DefaultConfig());
        var result = analyzer.Analyze(state.AllFlights(), sched);

        Assert.Empty(result.Chain);
        Assert.Equal(0, result.TotalElapsedSeconds);
    }

    [Fact]
    public void Bottleneck_ThreeNodeChain_PicksLongestPath()
    {
        // Two parallel chains; verify the longer one wins.
        //   A -> B -> C   (length = 3 ops + 2 buffers)
        //   D             (single op)
        var cfg = DefaultConfig(opArrival: 600, opDeparture: 600, depBuffer: 200);
        var state = new AirportState(cfg);
        state.SubmitFlight(Flight("A", OperationType.Arrival,   Priority.Medium));
        state.SubmitFlight(Flight("B", OperationType.Departure, Priority.Medium, deps: new[] { "A" }));
        state.SubmitFlight(Flight("C", OperationType.Arrival,   Priority.Medium, deps: new[] { "B" }));
        state.SubmitFlight(Flight("D", OperationType.Departure, Priority.Medium));
        var sched = state.GenerateSchedule();

        var analyzer = new BottleneckAnalyzer(cfg);
        var result = analyzer.Analyze(state.AllFlights(), sched);

        Assert.Equal(3, result.Chain.Count);
        Assert.Equal("A", result.Chain[0].FlightNumber);
        Assert.Equal("B", result.Chain[1].FlightNumber);
        Assert.Equal("C", result.Chain[2].FlightNumber);
        Assert.Equal(600 + 200 + 600 + 200 + 600, result.TotalElapsedSeconds);
    }
}
