using System;
using System.Collections.Generic;
using System.Linq;
using AtcMcpServer.Domain;

namespace AtcMcpServer.Configuration;

/// <summary>
/// All airport limits, derived from environment variables at startup.
/// Built via <see cref="AirportConfigLoader.LoadFromEnvironment"/>; validation
/// runs at construction and throws <see cref="ConfigurationException"/> for any
/// invalid value (per R2b).
/// </summary>
public sealed record AirportConfig
{
    public required IReadOnlyList<Runway> Runways { get; init; }
    public required int GateCount { get; init; }
    public required int GroundCrewCount { get; init; }

    /// <summary>Min gap between two consecutive TAKEOFFS on the same runway (seconds).</summary>
    public required int SeparationBufferTakeoffSeconds { get; init; }

    /// <summary>Min gap between two consecutive LANDINGS on the same runway (seconds).</summary>
    public required int SeparationBufferLandingSeconds { get; init; }

    /// <summary>Min gap when consecutive ops on the same runway are of different types (sec).</summary>
    public required int SeparationBufferMixedSeconds { get; init; }

    /// <summary>Min gap before the SAME gate can be reused after a flight releases it (sec).</summary>
    public required int GateTurnaroundSeconds { get; init; }

    /// <summary>
    /// Min gap between a dependency's end and its dependant's start (sec). Per scenario 3
    /// of the brief: "configured dependency buffer should be respected".
    /// </summary>
    public required int DependencyBufferSeconds { get; init; }

    /// <summary>Operation duration for arrivals (sec) — wheels-down to gate-parked.</summary>
    public required int OperationDurationArrivalSeconds { get; init; }

    /// <summary>Operation duration for departures (sec) — pushback to wheels-up.</summary>
    public required int OperationDurationDepartureSeconds { get; init; }

    /// <summary>Maximum scheduling horizon (hours from scheduling-start instant).</summary>
    public required int SchedulingHorizonHours { get; init; }

    /// <summary>
    /// Anchor time for the schedule. Defaults to a fixed instant in tests for
    /// determinism (R11); production code at MCP entry uses DateTime.UtcNow.
    /// </summary>
    public DateTime SchedulingStartUtc { get; init; } = DateTime.UtcNow;

    public int RunwayCount => Runways.Count;
    public TimeSpan SchedulingHorizon => TimeSpan.FromHours(SchedulingHorizonHours);
}

/// <summary>Raised by validators when ENV-derived configuration is malformed or out of range.</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}
