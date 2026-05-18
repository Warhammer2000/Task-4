namespace AtcMcpServer.Domain;

/// <summary>
/// A single runway as configured at startup. Length + category drive runway-requirement
/// matching (R5a, scenario 2).
/// </summary>
public sealed record Runway(string Id, int LengthMeters, string Category)
{
    /// <summary>Does this runway satisfy the requirements declared on a flight?</summary>
    public bool SatisfiesRequirements(Flight flight)
    {
        if (flight.MinRunwayLengthMeters is int minLen && LengthMeters < minLen)
            return false;
        if (!string.IsNullOrEmpty(flight.RequiredRunwayCategory) &&
            !string.Equals(Category, flight.RequiredRunwayCategory, System.StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}

/// <summary>
/// A single gate. Gates are interchangeable — no per-gate capabilities in the brief —
/// so id is the only field. Number of gates is bounded by <see cref="Configuration.AirportConfig.GateCount"/>.
/// </summary>
public sealed record Gate(string Id);
