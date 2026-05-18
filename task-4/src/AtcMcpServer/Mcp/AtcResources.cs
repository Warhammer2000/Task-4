using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using AtcMcpServer.Domain;
using AtcMcpServer.State;
using ModelContextProtocol.Server;

namespace AtcMcpServer.Mcp;

/// <summary>
/// MCP resources — read-only views of airport state. AI clients can SUBSCRIBE
/// to these via the MCP resources/list and resources/read methods. We expose three
/// per the brief: the queue (with unscheduled + cancelled), runway availability,
/// and the chronological operation timeline.
/// </summary>
[McpServerResourceType]
public static class AtcResources
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ===========================================================================
    // Resource 1 — flight_queue
    // Includes ALL submitted flights regardless of state, so clients can find
    // unscheduled and cancelled flights here.
    // ===========================================================================
    [McpServerResource(UriTemplate = "atc://flights/queue", Name = "flight_queue", MimeType = "application/json"),
     Description("All submitted flights with their current status, including unscheduled and cancelled. Use this resource as the canonical source for 'what is in the system right now'.")]
    public static string FlightQueue(AirportState state)
    {
        var flights = state.AllFlights();
        var rows = flights.Select(f => new
        {
            flightNumber = f.FlightNumber,
            operation = f.Operation.ToString(),
            priority = f.Priority.ToString(),
            status = f.Status.ToString(),
            dependsOn = f.DependsOn,
            minRunwayLengthMeters = f.MinRunwayLengthMeters,
            requiredRunwayCategory = f.RequiredRunwayCategory,
            unscheduleReason = f.UnscheduleReason,
            unscheduleMessage = f.UnscheduleMessage,
            scheduledSlot = f.ScheduledEntry,
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            totalCount = rows.Count,
            byStatus = flights.GroupBy(f => f.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            flights = rows,
        }, JsonOptions);
    }

    // ===========================================================================
    // Resource 2 — runway availability and usage
    // ===========================================================================
    [McpServerResource(UriTemplate = "atc://runways", Name = "runways", MimeType = "application/json"),
     Description("Configured runways with their capabilities (length, category) and the slots already scheduled on each. Useful for spotting runway-bound bottlenecks.")]
    public static string Runways(AirportState state)
    {
        var config = state.Config;
        var last = state.LastResult;

        var rows = config.Runways.Select(r => new
        {
            id = r.Id,
            lengthMeters = r.LengthMeters,
            category = r.Category,
            scheduledSlots = last?.Scheduled
                .Where(s => s.RunwayId == r.Id)
                .OrderBy(s => s.StartUtc)
                .ToList(),
        });

        return JsonSerializer.Serialize(new
        {
            runwayCount = config.RunwayCount,
            separationBuffers = new
            {
                takeoffSeconds = config.SeparationBufferTakeoffSeconds,
                landingSeconds = config.SeparationBufferLandingSeconds,
                mixedSeconds = config.SeparationBufferMixedSeconds,
            },
            runways = rows,
        }, JsonOptions);
    }

    // ===========================================================================
    // Resource 3 — operation_timeline
    // Chronological list of every scheduled operation, sorted by start time.
    // ===========================================================================
    [McpServerResource(UriTemplate = "atc://timeline", Name = "operation_timeline", MimeType = "application/json"),
     Description("Chronological timeline of all scheduled airport operations, sorted ascending by start time. Use to inspect the order in which flights will execute and to render a visual schedule.")]
    public static string OperationTimeline(AirportState state)
    {
        var last = state.LastResult;
        if (last is null)
        {
            return JsonSerializer.Serialize(new
            {
                hasSchedule = false,
                operations = System.Array.Empty<object>(),
                note = "No schedule has been generated yet. Call the generate_schedule tool first.",
            }, JsonOptions);
        }

        var ordered = last.Scheduled.OrderBy(s => s.StartUtc).ToList();
        return JsonSerializer.Serialize(new
        {
            hasSchedule = true,
            scheduleGeneratedAtUtc = last.ScheduleGeneratedAtUtc,
            completionTimeUtc = last.CompletionTimeUtc,
            operationCount = ordered.Count,
            operations = ordered,
        }, JsonOptions);
    }
}
