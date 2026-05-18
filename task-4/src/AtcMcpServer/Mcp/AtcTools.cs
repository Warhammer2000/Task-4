using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using AtcMcpServer.Domain;
using AtcMcpServer.Scheduling;
using AtcMcpServer.State;
using ModelContextProtocol.Server;

namespace AtcMcpServer.Mcp;

/// <summary>
/// All MCP tools the server exposes. One static class so the SDK can discover them
/// via WithToolsFromAssembly(). Every tool receives <see cref="AirportState"/> as a
/// DI parameter — the server is constructed with AirportState as a singleton.
///
/// Each tool returns a plain string (JSON when structured). The MCP SDK auto-wraps
/// the return value in a TextContent block; clients (Claude Desktop, mcp-inspector)
/// render it as-is. We avoid wrapping with our own object types to keep the surface
/// area minimal — the LLM sees consistent JSON and can reason on it directly.
/// </summary>
[McpServerToolType]
public static class AtcTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ===========================================================================
    // Tool 1 — submit_flight
    // ===========================================================================
    [McpServerTool(Name = "submit_flight"), Description(
        "Submit a new flight to the airport's pending queue. Use this BEFORE calling " +
        "generate_schedule so the new flight is considered in the next plan. Throws " +
        "if the flight number already exists or any declared dependency is unknown.")]
    public static string SubmitFlight(
        AirportState state,
        [Description("Globally unique flight identifier, e.g. 'BA101' or 'LH202'.")]
            string flightNumber,
        [Description("Operation type: 'arrival' (inbound landing) or 'departure' (outbound takeoff).")]
            string operationType,
        [Description("Priority level: 'high', 'medium', or 'low'. Higher priority is preferred when resources are contested.")]
            string priority,
        [Description("OPTIONAL: comma-separated list of other flight numbers this flight depends on (e.g. 'BA101' or 'BA101,LH202'). The dependant cannot start until ALL its deps end + the configured dependency buffer.")]
            string? dependsOn = null,
        [Description("OPTIONAL: minimum runway length in meters this flight requires. Wide-body / heavy aircraft typically need 3500-4000m+.")]
            int? minRunwayLengthMeters = null,
        [Description("OPTIONAL: required runway category (free-form label matched against the airport's configured runway categories, e.g. 'heavy', 'medium', 'short').")]
            string? requiredRunwayCategory = null)
    {
        if (!Enum.TryParse<OperationType>(operationType, ignoreCase: true, out var op))
            return ErrorJson($"Invalid operationType '{operationType}'. Expected 'arrival' or 'departure'.");
        if (!Enum.TryParse<Priority>(priority, ignoreCase: true, out var pri))
            return ErrorJson($"Invalid priority '{priority}'. Expected 'high', 'medium', or 'low'.");

        // dependsOn arrives as a comma-separated string from the MCP client.
        // We tried `string[]?` originally — but the ModelContextProtocol SDK
        // 1.3.0 emits a schema fragment without a `type` field for nullable
        // arrays, which makes every JSON-RPC client send the value as a
        // string anyway (or skip it). A scalar string with comma-split is
        // unambiguous, schema-clean, and matches how LLM clients tend to
        // produce list-typed inputs.
        var deps = string.IsNullOrWhiteSpace(dependsOn)
            ? System.Array.Empty<string>()
            : dependsOn.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

        var flight = new Flight
        {
            FlightNumber = flightNumber,
            Operation = op,
            Priority = pri,
            DependsOn = deps,
            MinRunwayLengthMeters = minRunwayLengthMeters,
            RequiredRunwayCategory = requiredRunwayCategory,
        };

        try
        {
            state.SubmitFlight(flight);
        }
        catch (InvalidOperationException ex)
        {
            return ErrorJson(ex.Message);
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            flightNumber,
            status = "queued",
            message = $"Flight {flightNumber} accepted. Call generate_schedule to place it.",
        }, JsonOptions);
    }

    // ===========================================================================
    // Tool 2 — generate_schedule
    // ===========================================================================
    [McpServerTool(Name = "generate_schedule"), Description(
        "Recompute the airport schedule from scratch based on the current queue and " +
        "configuration. Replaces any previous schedule. Returns the full result: " +
        "scheduled slots, unscheduled flights with reasons, and timestamps.")]
    public static string GenerateSchedule(AirportState state)
    {
        var result = state.GenerateSchedule();
        return JsonSerializer.Serialize(new
        {
            scheduleGeneratedAtUtc = result.ScheduleGeneratedAtUtc,
            completionTimeUtc = result.CompletionTimeUtc,
            scheduledCount = result.Scheduled.Count,
            unscheduledCount = result.Unscheduled.Count,
            scheduled = result.Scheduled,
            unscheduled = result.Unscheduled,
        }, JsonOptions);
    }

    // ===========================================================================
    // Tool 3 — airport_status (R9)
    // ===========================================================================
    [McpServerTool(Name = "airport_status"), Description(
        "Get a structured snapshot of the current airport state: flight counts by " +
        "status and operation type, runway and gate capacity/usage, resource " +
        "constraints, unscheduled flights with reasons, and the current schedule " +
        "completion time when a schedule has been generated.")]
    public static string AirportStatus(AirportState state)
    {
        var flights = state.AllFlights();
        var config = state.Config;
        var last = state.LastResult;

        var byStatus = flights.GroupBy(f => f.Status).ToDictionary(g => g.Key.ToString(), g => g.Count());
        var byOpType = flights.GroupBy(f => f.Operation).ToDictionary(g => g.Key.ToString(), g => g.Count());

        // Runway usage = number of scheduled ops per runway
        var runwayUsage = config.Runways.ToDictionary(
            r => r.Id,
            r => new
            {
                lengthMeters = r.LengthMeters,
                category = r.Category,
                scheduledOps = last?.Scheduled.Count(s => s.RunwayId == r.Id) ?? 0,
            });

        var gateUsage = Enumerable.Range(1, config.GateCount).ToDictionary(
            i => $"G{i}",
            i => last?.Scheduled.Count(s => s.GateId == $"G{i}") ?? 0);

        var unscheduled = flights
            .Where(f => f.Status == FlightStatus.Unscheduled)
            .Select(f => new
            {
                flightNumber = f.FlightNumber,
                operation = f.Operation.ToString(),
                priority = f.Priority.ToString(),
                reasonCode = f.UnscheduleReason,
                reasonMessage = f.UnscheduleMessage,
            })
            .ToList();

        // Resource constraint indicators: heuristic from the last run.
        var constraints = new List<string>();
        if (last != null)
        {
            int runwayBound = last.Unscheduled.Count(u => u.ReasonCode is UnscheduleReasons.NoSuitableRunway or UnscheduleReasons.NoResourceSlotInHorizon);
            if (runwayBound > 0) constraints.Add("runway_capacity");
            int depBlocked = last.Unscheduled.Count(u =>
                u.ReasonCode is UnscheduleReasons.DependencyCancelled
                              or UnscheduleReasons.DependencyMissing
                              or UnscheduleReasons.DependencyUnscheduled
                              or UnscheduleReasons.DependencyCycle);
            if (depBlocked > 0) constraints.Add("dependency_blockers");
        }

        return JsonSerializer.Serialize(new
        {
            flightCountsByStatus = byStatus,
            flightCountsByOperationType = byOpType,
            runway = new
            {
                capacity = config.RunwayCount,
                usage = runwayUsage,
            },
            gate = new
            {
                capacity = config.GateCount,
                usage = gateUsage,
            },
            groundCrewCapacity = config.GroundCrewCount,
            resourceConstraintIndicators = constraints,
            unscheduledFlightsWithReasons = unscheduled,
            scheduleCompletionTimeUtc = last?.CompletionTimeUtc,
            hasSchedule = last != null,
        }, JsonOptions);
    }

    // ===========================================================================
    // Tool 4 — cancel_flight (R8)
    // ===========================================================================
    [McpServerTool(Name = "cancel_flight"), Description(
        "Cancel a flight. The flight is marked as Cancelled and any flights that " +
        "depend on it are flipped back to the queue for re-evaluation on the next " +
        "generate_schedule call. Returns false if the flight does not exist or was " +
        "already cancelled.")]
    public static string CancelFlight(
        AirportState state,
        [Description("Flight number to cancel.")] string flightNumber)
    {
        bool ok = state.CancelFlight(flightNumber);
        if (!ok)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                message = $"Flight '{flightNumber}' not found or already cancelled.",
            }, JsonOptions);
        }

        // List the flights that were flipped back to the queue (dependants).
        var dependants = state.AllFlights()
            .Where(f => f.Status == FlightStatus.Queued && f.DependsOn.Contains(flightNumber, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.FlightNumber)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            cancelledFlight = flightNumber,
            dependantsRequeuedForReEvaluation = dependants,
            note = "Run generate_schedule to reflect cancellation in the timeline.",
        }, JsonOptions);
    }

    // ===========================================================================
    // Tool 5 — bottleneck_analysis (R10)
    // ===========================================================================
    [McpServerTool(Name = "bottleneck_analysis"), Description(
        "Identify the longest active scheduled dependency chain. Returns the ordered " +
        "flights, their start/end times, and the total elapsed duration in seconds " +
        "(accounting for operation durations + dependency buffers). Also returns a " +
        "Mermaid Gantt diagram of the chain — paste-renderable in chat clients.")]
    public static string BottleneckAnalysis(AirportState state)
    {
        var last = state.LastResult;
        if (last is null)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                message = "No schedule has been generated yet. Call generate_schedule first.",
            }, JsonOptions);
        }

        var analyzer = new BottleneckAnalyzer(state.Config);
        var result = analyzer.Analyze(state.AllFlights(), last);

        return JsonSerializer.Serialize(new
        {
            chainLength = result.Chain.Count,
            totalElapsedSeconds = result.TotalElapsedSeconds,
            totalElapsedHuman = HumanDuration(result.TotalElapsedSeconds),
            chain = result.Chain,
            mermaidGantt = result.MermaidGantt,
        }, JsonOptions);
    }

    // ===========================================================================
    // Helpers
    // ===========================================================================
    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, JsonOptions);

    private static string HumanDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{seconds}s";
    }
}
