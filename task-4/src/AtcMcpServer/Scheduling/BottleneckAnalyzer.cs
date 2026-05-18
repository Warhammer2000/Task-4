using System.Collections.Generic;
using System.Linq;
using System.Text;
using AtcMcpServer.Configuration;
using AtcMcpServer.Domain;

namespace AtcMcpServer.Scheduling;

/// <summary>
/// Identifies the longest active dependency chain in a generated schedule, per R10.
/// The "weight" of an edge from dep -> dependant is the dep's operation duration plus
/// the configured dependency buffer (R10c, R10d). The reported total elapsed duration
/// is the sum of node operation durations along the chain PLUS the (n-1) dependency
/// buffers separating them.
///
/// Only flights that ended up Scheduled are considered (per the brief — "longest active
/// scheduled dependency chain").
///
/// Renders an optional Mermaid Gantt block for visual rendering by the AI client.
/// </summary>
public sealed class BottleneckAnalyzer
{
    private readonly AirportConfig _config;

    public BottleneckAnalyzer(AirportConfig config) => _config = config;

    public BottleneckResult Analyze(IReadOnlyList<Flight> allFlights, ScheduleResult lastSchedule)
    {
        // Index scheduled flights only — bottleneck on cancelled / unscheduled flights
        // would mislead, since they don't have a real position in the timeline.
        var scheduledFlights = allFlights
            .Where(f => f.Status == FlightStatus.Scheduled && f.ScheduledEntry is not null)
            .ToDictionary(f => f.FlightNumber, System.StringComparer.OrdinalIgnoreCase);

        if (scheduledFlights.Count == 0)
        {
            return new BottleneckResult(
                Chain: System.Array.Empty<BottleneckNode>(),
                TotalElapsedSeconds: 0,
                MermaidGantt: null);
        }

        // Reverse adjacency: child -> parent (we walk forward by extending from a parent).
        // For longest-path DP we evaluate each node's "best chain ending here".
        // chainEnd[node] = (length_seconds, predecessor)
        var chainEnd = new Dictionary<string, (int len, string? prev)>(System.StringComparer.OrdinalIgnoreCase);

        // Sort by schedule start time so dependencies are processed before dependants
        // (since a flight cannot start before its dep ends, dep.StartUtc < this.StartUtc).
        var orderedByStart = scheduledFlights.Values
            .OrderBy(f => f.ScheduledEntry!.StartUtc)
            .ThenBy(f => f.FlightNumber, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var f in orderedByStart)
        {
            int ownDuration = (int)(f.ScheduledEntry!.EndUtc - f.ScheduledEntry.StartUtc).TotalSeconds;

            // Find the best predecessor among this flight's deps that are actually scheduled.
            int bestPredLen = 0;
            string? bestPredName = null;
            foreach (var depName in f.DependsOn)
            {
                if (chainEnd.TryGetValue(depName, out var depChain))
                {
                    // Extending the chain through depName adds: dep's accumulated chain
                    // length + the dependency buffer between dep and this flight.
                    int candidate = depChain.len + _config.DependencyBufferSeconds;
                    if (candidate > bestPredLen)
                    {
                        bestPredLen = candidate;
                        bestPredName = depName;
                    }
                }
            }
            chainEnd[f.FlightNumber] = (bestPredLen + ownDuration, bestPredName);
        }

        // Pick the node with the largest accumulated chain length.
        var terminal = chainEnd.OrderByDescending(kv => kv.Value.len)
                               .ThenBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase)
                               .First();

        // Reconstruct chain by walking predecessors backwards.
        var chainReversed = new List<string>();
        var cursor = (string?)terminal.Key;
        while (cursor != null)
        {
            chainReversed.Add(cursor);
            cursor = chainEnd[cursor].prev;
        }
        chainReversed.Reverse();

        // If the "chain" is a single isolated node with no deps, the brief leaves it
        // ambiguous whether to surface it. We surface anyway — the longest "chain"
        // of one flight is just that flight; clients can decide whether that's
        // interesting based on the chain length being == operation duration.
        var nodes = chainReversed
            .Select(name => scheduledFlights[name])
            .Select(f => new BottleneckNode(
                f.FlightNumber, f.Operation, f.Priority,
                f.ScheduledEntry!.StartUtc, f.ScheduledEntry.EndUtc,
                f.ScheduledEntry.RunwayId, f.ScheduledEntry.GateId))
            .ToList();

        var mermaid = BuildMermaidGantt(nodes);
        return new BottleneckResult(nodes, terminal.Value.len, mermaid);
    }

    /// <summary>
    /// Render the chain as a Mermaid Gantt block. Claude clients (and most chat
    /// surfaces with markdown rendering) display this as an actual diagram, which
    /// turns "tell me the bottleneck" into a single-shot visual answer.
    /// </summary>
    private static string BuildMermaidGantt(IReadOnlyList<BottleneckNode> chain)
    {
        if (chain.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("gantt");
        sb.AppendLine("    title Critical dependency chain");
        sb.AppendLine("    dateFormat  YYYY-MM-DDTHH:mm:ss");
        sb.AppendLine("    axisFormat  %H:%M");
        sb.AppendLine("    section Bottleneck");
        foreach (var n in chain)
        {
            var safeLabel = n.FlightNumber.Replace(":", "_").Replace(",", "_");
            sb.AppendLine(
                $"    {safeLabel} ({n.Operation}, {n.Priority}, runway {n.RunwayId}) :{n.StartUtc:yyyy-MM-ddTHH:mm:ss}, {n.EndUtc:yyyy-MM-ddTHH:mm:ss}");
        }
        sb.Append("```");
        return sb.ToString();
    }
}

/// <summary>One step in the critical chain — fully self-describing for AI clients.</summary>
public sealed record BottleneckNode(
    string FlightNumber,
    OperationType Operation,
    Priority Priority,
    System.DateTime StartUtc,
    System.DateTime EndUtc,
    string RunwayId,
    string GateId);

/// <summary>
/// Output of <see cref="BottleneckAnalyzer.Analyze"/>. The MermaidGantt string is
/// a fenced ```mermaid``` block ready to paste into any markdown surface — Claude
/// Desktop and most chat clients render it visually.
/// </summary>
public sealed record BottleneckResult(
    IReadOnlyList<BottleneckNode> Chain,
    int TotalElapsedSeconds,
    string? MermaidGantt);
