using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AtcMcpServer.Configuration;
using AtcMcpServer.Domain;
using AtcMcpServer.Scheduling;

namespace AtcMcpServer.State;

/// <summary>
/// Single in-memory home for all airport state: configured runways/gates, submitted
/// flights, and the last scheduler result. All mutations go through this type — the
/// MCP tool handlers do not touch domain collections directly.
///
/// Thread safety: every public mutator takes <see cref="_lock"/> exclusively, which
/// also guarantees the determinism invariant (R11) — two GenerateSchedule calls
/// against the same flight set always see the same submission-order numbering, in the
/// same lock-acquisition order.
/// </summary>
public sealed class AirportState
{
    private readonly object _lock = new();
    private readonly AirportConfig _config;
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.OrdinalIgnoreCase);
    private int _nextSubmissionOrder;
    private ScheduleResult? _lastResult;

    public AirportState(AirportConfig config) => _config = config;

    public AirportConfig Config => _config;

    // ---- Flight queue management -----------------------------------------

    /// <summary>
    /// Submit a flight. Throws ONLY on duplicate flight number.
    ///
    /// Dependency validation is deliberately deferred to schedule time. Reason:
    /// MCP clients (Claude Desktop, the SDK's worker pool, etc.) may dispatch
    /// concurrent submit_flight tool calls. The lock in this method guarantees
    /// no data corruption, but NOT submission order — a request submitting
    /// flight B that depends on A can be processed before A's submission even
    /// when A was sent first over the wire.
    ///
    /// Rejecting B at submission time in that case is a false negative. The
    /// Scheduler already has a <see cref="UnscheduleReasons.DependencyMissing"/>
    /// path for the "still unknown at scheduling time" case, which is the
    /// correct moment to surface the failure.
    /// </summary>
    public void SubmitFlight(Flight flight)
    {
        lock (_lock)
        {
            if (_flights.ContainsKey(flight.FlightNumber))
                throw new InvalidOperationException(
                    $"Flight '{flight.FlightNumber}' already submitted. Use cancel + resubmit if you need to replace it.");

            flight.SubmissionOrder = Interlocked.Increment(ref _nextSubmissionOrder);
            flight.Status = FlightStatus.Queued;
            _flights[flight.FlightNumber] = flight;
        }
    }

    /// <summary>
    /// Mark a flight as cancelled. Per R8, dependent flights are flagged Queued
    /// so that the next GenerateSchedule call re-evaluates them. We do NOT
    /// transitively cancel — that's an operator policy decision, not the brief's.
    /// </summary>
    public bool CancelFlight(string flightNumber)
    {
        lock (_lock)
        {
            if (!_flights.TryGetValue(flightNumber, out var flight))
                return false;
            if (flight.Status == FlightStatus.Cancelled)
                return false;

            flight.Status = FlightStatus.Cancelled;
            flight.ScheduledEntry = null;
            flight.UnscheduleReason = null;
            flight.UnscheduleMessage = null;

            // R8b: dependents must be re-evaluated. Cheapest correct way: flip them
            // back to Queued so the next GenerateSchedule reconsiders them with the
            // cancellation visible.
            foreach (var f in _flights.Values)
            {
                if (f.DependsOn.Contains(flightNumber, StringComparer.OrdinalIgnoreCase))
                {
                    if (f.Status is FlightStatus.Scheduled or FlightStatus.Unscheduled)
                    {
                        f.Status = FlightStatus.Queued;
                        f.ScheduledEntry = null;
                        f.UnscheduleReason = null;
                        f.UnscheduleMessage = null;
                    }
                }
            }
            return true;
        }
    }

    // ---- Schedule generation ---------------------------------------------

    /// <summary>
    /// "Calling this tool replaces the current schedule with a freshly computed one
    /// based on the current flight queue and airport configuration" — brief.
    /// </summary>
    public ScheduleResult GenerateSchedule()
    {
        lock (_lock)
        {
            // Reset prior scheduling decisions (but keep Cancelled flags).
            foreach (var f in _flights.Values)
            {
                if (f.Status != FlightStatus.Cancelled)
                {
                    f.Status = FlightStatus.Queued;
                    f.ScheduledEntry = null;
                    f.UnscheduleReason = null;
                    f.UnscheduleMessage = null;
                }
            }

            var scheduler = new Scheduler(_config);
            // Pass ALL flights including cancelled — the scheduler needs visibility on
            // cancelled flights to report DEPENDENCY_CANCELLED on their dependants (R8b).
            // The scheduler skips cancelled flights internally for placement.
            var snapshot = _flights.Values.ToList();
            _lastResult = scheduler.Schedule(snapshot);

            // Write the slots back onto the domain objects so AirportState
            // remains the single source of truth for "current state".
            var byNumber = _lastResult.Scheduled.ToDictionary(e => e.FlightNumber);
            foreach (var f in _flights.Values)
            {
                if (f.Status == FlightStatus.Cancelled) continue;
                if (byNumber.TryGetValue(f.FlightNumber, out var entry))
                {
                    f.Status = FlightStatus.Scheduled;
                    f.ScheduledEntry = entry;
                }
                else
                {
                    var u = _lastResult.Unscheduled.FirstOrDefault(x => x.FlightNumber == f.FlightNumber);
                    f.Status = FlightStatus.Unscheduled;
                    f.UnscheduleReason = u?.ReasonCode;
                    f.UnscheduleMessage = u?.ReasonMessage;
                }
            }

            return _lastResult;
        }
    }

    // ---- Read-side ---------------------------------------------------------

    public IReadOnlyList<Flight> AllFlights()
    {
        lock (_lock) return _flights.Values.OrderBy(f => f.SubmissionOrder).ToList();
    }

    public Flight? TryGetFlight(string flightNumber)
    {
        lock (_lock) return _flights.TryGetValue(flightNumber, out var f) ? f : null;
    }

    public ScheduleResult? LastResult
    {
        get { lock (_lock) return _lastResult; }
    }

    /// <summary>For tests only — start over with a clean state.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _flights.Clear();
            _nextSubmissionOrder = 0;
            _lastResult = null;
        }
    }
}
