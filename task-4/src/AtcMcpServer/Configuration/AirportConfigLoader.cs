using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AtcMcpServer.Domain;

namespace AtcMcpServer.Configuration;

/// <summary>
/// Builds an <see cref="AirportConfig"/> from process environment variables.
/// All variables are prefixed with <c>ATC_</c>. Every required variable produces
/// a precise error message identifying which variable failed and why — per R2b
/// of the brief ("invalid configuration should fail clearly at startup").
/// </summary>
public static class AirportConfigLoader
{
    /// <summary>Reads from <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    public static AirportConfig LoadFromEnvironment()
        => LoadFrom(name => Environment.GetEnvironmentVariable(name));

    /// <summary>Pure overload taking a getter — used from tests.</summary>
    public static AirportConfig LoadFrom(Func<string, string?> getEnv)
    {
        var runways = ParseRunways(getEnv);
        var gateCount = RequireInt(getEnv, "ATC_GATE_COUNT", min: 1);
        var groundCrew = RequireInt(getEnv, "ATC_GROUND_CREW_COUNT", min: 1);

        var sepTakeoff = RequireInt(getEnv, "ATC_SEPARATION_BUFFER_TAKEOFF_S", min: 0);
        var sepLanding = RequireInt(getEnv, "ATC_SEPARATION_BUFFER_LANDING_S", min: 0);
        var sepMixed = RequireInt(getEnv, "ATC_SEPARATION_BUFFER_MIXED_S", min: 0);
        var turnaround = RequireInt(getEnv, "ATC_GATE_TURNAROUND_TIME_S", min: 0);
        var depBuffer = RequireInt(getEnv, "ATC_DEPENDENCY_BUFFER_S", min: 0);
        var opArrival = RequireInt(getEnv, "ATC_OP_DURATION_ARRIVAL_S", min: 1);
        var opDeparture = RequireInt(getEnv, "ATC_OP_DURATION_DEPARTURE_S", min: 1);
        var horizon = RequireInt(getEnv, "ATC_SCHEDULING_HORIZON_HOURS", min: 1, max: 168);

        return new AirportConfig
        {
            Runways = runways,
            GateCount = gateCount,
            GroundCrewCount = groundCrew,
            SeparationBufferTakeoffSeconds = sepTakeoff,
            SeparationBufferLandingSeconds = sepLanding,
            SeparationBufferMixedSeconds = sepMixed,
            GateTurnaroundSeconds = turnaround,
            DependencyBufferSeconds = depBuffer,
            OperationDurationArrivalSeconds = opArrival,
            OperationDurationDepartureSeconds = opDeparture,
            SchedulingHorizonHours = horizon,
        };
    }

    private static IReadOnlyList<Runway> ParseRunways(Func<string, string?> getEnv)
    {
        // Format: ATC_RUNWAYS=R1:3000:medium,R2:4000:heavy,R3:2500:short
        // Each entry: id:length_meters:category
        var raw = getEnv("ATC_RUNWAYS");
        if (string.IsNullOrWhiteSpace(raw))
            throw new ConfigurationException(
                "ATC_RUNWAYS is required. Format: id:length_m:category,id:length_m:category " +
                "(e.g. 'R1:3000:medium,R2:4000:heavy'). At least one runway must be declared.");

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ConfigurationException(
                "ATC_RUNWAYS is empty after splitting. Provide at least one runway entry.");

        var runways = new List<Runway>(parts.Length);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            var fields = part.Split(':', StringSplitOptions.TrimEntries);
            if (fields.Length != 3)
                throw new ConfigurationException(
                    $"ATC_RUNWAYS entry '{part}' malformed. Expected 'id:length_m:category', got {fields.Length} fields.");
            var id = fields[0];
            if (string.IsNullOrWhiteSpace(id))
                throw new ConfigurationException($"ATC_RUNWAYS entry '{part}' has empty id.");
            if (!seenIds.Add(id))
                throw new ConfigurationException($"ATC_RUNWAYS has duplicate runway id '{id}'.");
            if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var len))
                throw new ConfigurationException($"ATC_RUNWAYS runway '{id}' length '{fields[1]}' is not an integer.");
            if (len <= 0)
                throw new ConfigurationException($"ATC_RUNWAYS runway '{id}' length must be > 0 (got {len}).");
            var cat = fields[2];
            if (string.IsNullOrWhiteSpace(cat))
                throw new ConfigurationException($"ATC_RUNWAYS runway '{id}' category is empty.");
            runways.Add(new Runway(id, len, cat.ToLowerInvariant()));
        }

        // Sanity: optional ATC_RUNWAY_COUNT cross-check.
        var declaredCount = getEnv("ATC_RUNWAY_COUNT");
        if (!string.IsNullOrWhiteSpace(declaredCount) &&
            int.TryParse(declaredCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dc) &&
            dc != runways.Count)
        {
            throw new ConfigurationException(
                $"ATC_RUNWAY_COUNT ({dc}) does not match number of ATC_RUNWAYS entries ({runways.Count}).");
        }

        return runways;
    }

    private static int RequireInt(Func<string, string?> getEnv, string name, int? min = null, int? max = null)
    {
        var raw = getEnv(name);
        if (string.IsNullOrWhiteSpace(raw))
            throw new ConfigurationException($"{name} is required (set in environment).");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ConfigurationException($"{name}='{raw}' is not a valid integer.");
        if (min is int m && value < m)
            throw new ConfigurationException($"{name}={value} is below minimum allowed value {m}.");
        if (max is int mx && value > mx)
            throw new ConfigurationException($"{name}={value} is above maximum allowed value {mx}.");
        return value;
    }
}
