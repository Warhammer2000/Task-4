using System.Collections.Generic;
using AtcMcpServer.Configuration;
using Xunit;

namespace AtcMcpServer.Tests;

/// <summary>
/// R2b — "invalid configuration should fail clearly at startup". Every env var
/// should produce a precise <see cref="ConfigurationException"/> when bad or missing.
/// </summary>
public class ConfigValidationTests
{
    [Fact]
    public void Missing_ATC_RUNWAYS_FailsClearly()
    {
        var env = ValidEnv();
        env.Remove("ATC_RUNWAYS");
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("ATC_RUNWAYS", ex.Message);
    }

    [Fact]
    public void Malformed_ATC_RUNWAYS_FailsClearly()
    {
        var env = ValidEnv();
        env["ATC_RUNWAYS"] = "R1:notanumber:medium";
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("integer", ex.Message);
    }

    [Fact]
    public void Duplicate_RunwayId_FailsClearly()
    {
        var env = ValidEnv();
        env["ATC_RUNWAYS"] = "R1:3000:medium,R1:4000:heavy";
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("duplicate", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_GateCount_FailsClearly()
    {
        var env = ValidEnv();
        env.Remove("ATC_GATE_COUNT");
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("ATC_GATE_COUNT", ex.Message);
    }

    [Fact]
    public void Negative_Buffer_FailsClearly()
    {
        var env = ValidEnv();
        env["ATC_SEPARATION_BUFFER_TAKEOFF_S"] = "-1";
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("ATC_SEPARATION_BUFFER_TAKEOFF_S", ex.Message);
    }

    [Fact]
    public void Mismatched_RunwayCount_FailsClearly()
    {
        var env = ValidEnv();
        env["ATC_RUNWAY_COUNT"] = "5";  // declared 5 but only 2 listed
        var ex = Assert.Throws<ConfigurationException>(() => AirportConfigLoader.LoadFrom(name => env.GetValueOrDefault(name)));
        Assert.Contains("ATC_RUNWAY_COUNT", ex.Message);
    }

    [Fact]
    public void Valid_Config_Loads_Successfully()
    {
        var cfg = AirportConfigLoader.LoadFrom(name => ValidEnv().GetValueOrDefault(name));
        Assert.Equal(2, cfg.Runways.Count);
        Assert.Equal(4, cfg.GateCount);
    }

    private static Dictionary<string, string?> ValidEnv() => new()
    {
        ["ATC_RUNWAYS"] = "R1:3500:medium,R2:4000:heavy",
        ["ATC_GATE_COUNT"] = "4",
        ["ATC_GROUND_CREW_COUNT"] = "4",
        ["ATC_SEPARATION_BUFFER_TAKEOFF_S"] = "60",
        ["ATC_SEPARATION_BUFFER_LANDING_S"] = "90",
        ["ATC_SEPARATION_BUFFER_MIXED_S"] = "120",
        ["ATC_GATE_TURNAROUND_TIME_S"] = "600",
        ["ATC_DEPENDENCY_BUFFER_S"] = "300",
        ["ATC_OP_DURATION_ARRIVAL_S"] = "600",
        ["ATC_OP_DURATION_DEPARTURE_S"] = "480",
        ["ATC_SCHEDULING_HORIZON_HOURS"] = "12",
    };
}
