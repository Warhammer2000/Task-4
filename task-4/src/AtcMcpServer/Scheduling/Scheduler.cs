using System;
using System.Collections.Generic;
using System.Linq;
using AtcMcpServer.Configuration;
using AtcMcpServer.Domain;

namespace AtcMcpServer.Scheduling;

/// <summary>
/// Greedy, deterministic scheduler.
///
/// Algorithm (one pass, sequential placement):
///   1. Build a stable order of flights:
///        a. dependency depth ascending  (flights with no deps go first)
///        b. priority ascending          (High before Low)
///        c. submission order ascending  (deterministic tie-break — R11)
///
///      Dependency depth comes BEFORE priority because a high-priority flight that
///      depends on a low-priority flight still has to wait — so we want the dep
///      placed first to know its end time.
///
///   2. For each flight in that order:
///        a. compute earliest_start = max(scheduling_start,
///                                        all-deps-end + dependency_buffer)
///        b. iterate over (runway, gate) pairs where runway satisfies the flight's
///           capability requirements
///        c. for each pair, find the earliest slot that:
///             - does not overlap any prior slot on that runway or that gate
///             - respects the separation buffer vs the LAST op on that runway
///             - respects the gate turnaround buffer
///             - does not exceed the scheduling horizon
///        d. pick the slot with the smallest start time across all (runway, gate)
///           combinations; ties broken by (runway id, gate id) lex order
///        e. if nothing fits within the horizon — Unscheduled with reason
///        f. if no runway is even capable — NO_SUITABLE_RUNWAY (R5a, scenario 2)
///
/// This is O(F * R * G * F) in the worst case — fine for the small scales the brief
/// targets. For thousands of flights you'd want an interval tree or per-resource
/// availability stream; deliberately out of scope here.
/// </summary>
public sealed class Scheduler
{
    private readonly AirportConfig _config;
    private readonly List<Gate> _gates;

    public Scheduler(AirportConfig config)
    {
        _config = config;
        // Gates are interchangeable; generate stable ids G1..GN so test output is readable.
        _gates = Enumerable.Range(1, config.GateCount)
                           .Select(i => new Gate($"G{i}"))
                           .ToList();
    }

    public ScheduleResult Schedule(IEnumerable<Flight> flights)
    {
        var allInput = flights.ToList();
        var generatedAt = _config.SchedulingStartUtc;
        var horizonEnd = generatedAt + _config.SchedulingHorizon;

        // Cancelled flights stay in byNumber so dependants can be reported with the
        // DEPENDENCY_CANCELLED reason (R8b) — but they are not themselves considered
        // for placement.
        var byNumber = allInput.ToDictionary(f => f.FlightNumber, StringComparer.OrdinalIgnoreCase);
        var pool = allInput.Where(f => f.Status != FlightStatus.Cancelled).ToList();

        // === Dependency depth (with cycle detection) =========================
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cycleVictims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in pool)
            ComputeDepth(f, byNumber, depth, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cycleVictims);

        // === Build placement order ===========================================
        var ordered = pool
            .OrderBy(f => depth.GetValueOrDefault(f.FlightNumber, int.MaxValue))
            .ThenBy(f => (int)f.Priority)
            .ThenBy(f => f.SubmissionOrder)
            .ToList();

        // === Track per-resource usage ========================================
        // Slots tracked per runway and per gate so overlaps can be detected by
        // simple list scans. Separation buffer for runways is computed against
        // the LAST op (largest EndOffset) on that runway.
        var runwaySlots = _config.Runways.ToDictionary(r => r.Id, _ => new List<Slot>(), StringComparer.OrdinalIgnoreCase);
        var gateSlots = _gates.ToDictionary(g => g.Id, _ => new List<Slot>(), StringComparer.OrdinalIgnoreCase);

        // Cumulative timeline of ops to enforce ground-crew capacity (R5e).
        var allOps = new List<Slot>();

        var scheduled = new List<ScheduleEntry>();
        var unscheduled = new List<UnscheduledEntry>();
        var endOffsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in ordered)
        {
            // --- Cycle short-circuit ----------------------------------------
            if (cycleVictims.Contains(f.FlightNumber))
            {
                unscheduled.Add(new UnscheduledEntry(f.FlightNumber, f.Operation, f.Priority,
                    UnscheduleReasons.DependencyCycle,
                    "Flight participates in a cyclic dependency and cannot be scheduled."));
                continue;
            }

            // --- Dependencies -----------------------------------------------
            int earliestStart = 0;
            string? blockingReason = null;
            string? blockingMessage = null;
            foreach (var depName in f.DependsOn)
            {
                if (!byNumber.TryGetValue(depName, out var dep))
                {
                    blockingReason = UnscheduleReasons.DependencyMissing;
                    blockingMessage = $"Depends on unknown flight '{depName}'.";
                    break;
                }
                if (dep.Status == FlightStatus.Cancelled)
                {
                    blockingReason = UnscheduleReasons.DependencyCancelled;
                    blockingMessage = $"Depends on cancelled flight '{depName}'.";
                    break;
                }
                if (!endOffsets.TryGetValue(depName, out var depEnd))
                {
                    // Dep didn't end up scheduled in this run.
                    blockingReason = UnscheduleReasons.DependencyUnscheduled;
                    blockingMessage = $"Depends on flight '{depName}' which is not scheduled in this plan.";
                    break;
                }
                var afterDep = depEnd + _config.DependencyBufferSeconds;
                if (afterDep > earliestStart) earliestStart = afterDep;
            }
            if (blockingReason != null)
            {
                unscheduled.Add(new UnscheduledEntry(f.FlightNumber, f.Operation, f.Priority, blockingReason, blockingMessage!));
                continue;
            }

            // --- Runway capability check (R5a / scenario 2) -----------------
            var eligibleRunways = _config.Runways.Where(r => r.SatisfiesRequirements(f)).ToList();
            if (eligibleRunways.Count == 0)
            {
                unscheduled.Add(new UnscheduledEntry(f.FlightNumber, f.Operation, f.Priority,
                    UnscheduleReasons.NoSuitableRunway,
                    BuildNoRunwayMessage(f)));
                continue;
            }

            // --- Operation duration -----------------------------------------
            var duration = f.Operation == OperationType.Arrival
                ? _config.OperationDurationArrivalSeconds
                : _config.OperationDurationDepartureSeconds;

            // --- Find best (runway, gate, start) ----------------------------
            Slot? best = null;
            foreach (var runway in eligibleRunways)
            {
                foreach (var gate in _gates)
                {
                    var candidateStart = FindEarliestSlot(
                        runway, gate, runwaySlots, gateSlots, allOps,
                        earliestStart, duration, f.Operation);
                    if (candidateStart is null) continue;
                    var slot = new Slot(f.FlightNumber, runway.Id, gate.Id, f.Operation, f.Priority,
                                        candidateStart.Value, candidateStart.Value + duration);
                    if (slot.EndOffset > _config.SchedulingHorizon.TotalSeconds) continue;

                    // Prefer earliest start; ties broken by (runway id, gate id).
                    if (best is null
                        || slot.StartOffset < best.Value.StartOffset
                        || (slot.StartOffset == best.Value.StartOffset && StringComparer.OrdinalIgnoreCase.Compare(slot.RunwayId, best.Value.RunwayId) < 0)
                        || (slot.StartOffset == best.Value.StartOffset && slot.RunwayId == best.Value.RunwayId && StringComparer.OrdinalIgnoreCase.Compare(slot.GateId, best.Value.GateId) < 0))
                    {
                        best = slot;
                    }
                }
            }

            if (best is null)
            {
                unscheduled.Add(new UnscheduledEntry(f.FlightNumber, f.Operation, f.Priority,
                    UnscheduleReasons.NoResourceSlotInHorizon,
                    $"No (runway, gate) combination has a free slot within the scheduling horizon ({_config.SchedulingHorizonHours} h)."));
                continue;
            }

            runwaySlots[best.Value.RunwayId].Add(best.Value);
            gateSlots[best.Value.GateId].Add(best.Value);
            allOps.Add(best.Value);
            endOffsets[f.FlightNumber] = best.Value.EndOffset;

            scheduled.Add(new ScheduleEntry(
                f.FlightNumber, f.Operation, f.Priority,
                best.Value.RunwayId, best.Value.GateId,
                generatedAt.AddSeconds(best.Value.StartOffset),
                generatedAt.AddSeconds(best.Value.EndOffset)));
        }

        scheduled.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return new ScheduleResult(scheduled, unscheduled, generatedAt);
    }

    // ---- Helpers ---------------------------------------------------------

    /// <summary>
    /// Find the earliest start offset (sec since scheduling-start) where this flight
    /// can take BOTH the given runway AND the given gate without overlap, respecting
    /// separation and turnaround buffers and ground crew capacity.
    /// Returns null if no such offset exists within the scheduling horizon.
    /// </summary>
    private int? FindEarliestSlot(
        Runway runway, Gate gate,
        Dictionary<string, List<Slot>> runwaySlots,
        Dictionary<string, List<Slot>> gateSlots,
        List<Slot> allOps,
        int earliestStart, int duration, OperationType op)
    {
        var horizonSec = (int)_config.SchedulingHorizon.TotalSeconds;
        var start = earliestStart;
        // Iterate forward, jumping past every blocking constraint until either
        // (a) we find a clear slot or (b) we pass the horizon.
        // The jumps are large enough that loop iteration count is bounded by
        // (#slots-on-runway + #slots-on-gate + ground_crew_violation_count).
        while (start + duration <= horizonSec)
        {
            int? bump = null;

            // Runway availability + separation.
            foreach (var s in runwaySlots[runway.Id])
            {
                var sep = SeparationBufferSecondsForRunway(s.Operation, op);
                // Conflict if [start, start+dur) overlaps with [s.Start, s.End + sep)
                // (the buffer applies AFTER the prior op completes).
                int blockedUntil = s.EndOffset + sep;
                if (start < blockedUntil && start + duration > s.StartOffset)
                {
                    bump = bump.HasValue ? Math.Max(bump.Value, blockedUntil) : blockedUntil;
                }
            }

            // Gate availability + turnaround.
            foreach (var s in gateSlots[gate.Id])
            {
                int blockedUntil = s.EndOffset + _config.GateTurnaroundSeconds;
                if (start < blockedUntil && start + duration > s.StartOffset)
                {
                    bump = bump.HasValue ? Math.Max(bump.Value, blockedUntil) : blockedUntil;
                }
            }

            // Ground-crew capacity: max simultaneous ops <= ground_crew_count.
            // Count ops that overlap with [start, start+dur).
            if (bump is null)
            {
                int active = 0;
                foreach (var s in allOps)
                {
                    if (s.StartOffset < start + duration && s.EndOffset > start)
                        active++;
                }
                if (active >= _config.GroundCrewCount)
                {
                    // Bump to the earliest end among currently active ops.
                    int earliestFree = int.MaxValue;
                    foreach (var s in allOps)
                    {
                        if (s.StartOffset < start + duration && s.EndOffset > start)
                        {
                            if (s.EndOffset < earliestFree) earliestFree = s.EndOffset;
                        }
                    }
                    if (earliestFree != int.MaxValue) bump = earliestFree;
                }
            }

            if (bump is null) return start;
            start = bump.Value;
        }
        return null;
    }

    private int SeparationBufferSecondsForRunway(OperationType prev, OperationType next)
    {
        if (prev == next)
        {
            return next == OperationType.Departure
                ? _config.SeparationBufferTakeoffSeconds
                : _config.SeparationBufferLandingSeconds;
        }
        return _config.SeparationBufferMixedSeconds;
    }

    private static string BuildNoRunwayMessage(Flight f)
    {
        if (f.MinRunwayLengthMeters is int len && !string.IsNullOrEmpty(f.RequiredRunwayCategory))
            return $"No runway in the airport meets the requirements (>= {len} m AND category='{f.RequiredRunwayCategory}').";
        if (f.MinRunwayLengthMeters is int l)
            return $"No runway in the airport is long enough (required >= {l} m).";
        if (!string.IsNullOrEmpty(f.RequiredRunwayCategory))
            return $"No runway in the airport matches the required category '{f.RequiredRunwayCategory}'.";
        return "No suitable runway available (capability mismatch).";
    }

    private static int ComputeDepth(
        Flight f,
        Dictionary<string, Flight> byNumber,
        Dictionary<string, int> depth,
        HashSet<string> visiting,
        HashSet<string> cycleVictims)
    {
        if (depth.TryGetValue(f.FlightNumber, out var d)) return d;
        if (!visiting.Add(f.FlightNumber))
        {
            // Cycle: mark every flight in the visiting set as victim.
            foreach (var v in visiting) cycleVictims.Add(v);
            return depth[f.FlightNumber] = int.MaxValue;
        }
        int max = 0;
        foreach (var depName in f.DependsOn)
        {
            if (!byNumber.TryGetValue(depName, out var depFlight)) continue;
            var childDepth = ComputeDepth(depFlight, byNumber, depth, visiting, cycleVictims);
            if (childDepth + 1 > max) max = childDepth + 1;
        }
        visiting.Remove(f.FlightNumber);
        return depth[f.FlightNumber] = max;
    }

    /// <summary>
    /// Internal per-op record: integer seconds since scheduling-start. We do all
    /// scheduling arithmetic in ints to keep determinism (no float rounding) and
    /// convert to DateTime only at the boundary.
    /// </summary>
    internal readonly record struct Slot(
        string FlightNumber,
        string RunwayId,
        string GateId,
        OperationType Operation,
        Priority Priority,
        int StartOffset,
        int EndOffset);
}
