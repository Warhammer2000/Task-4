# Air Traffic Control — MCP Server

[![CI](https://github.com/Warhammer2000/Task-4/actions/workflows/ci.yml/badge.svg)](https://github.com/Warhammer2000/Task-4/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![MCP](https://img.shields.io/badge/MCP-1.3.0%20GA-7B61FF.svg)](https://www.nuget.org/packages/ModelContextProtocol)
[![Tests](https://img.shields.io/badge/tests-19%20passing-success.svg)](./tests/)

> AI-ready coordinator for a busy airport. Accepts flight plans, schedules arrivals & departures across runways, gates, and ground crew respecting separation, turnaround, and dependency buffers, then exposes the entire state to MCP-compatible AI clients (Claude Desktop, mcp-inspector, custom hosts).

**Built for:** Vention AI Challenge 2.0 · Task 4 — *MCPing*
**Stack:** C# / .NET 10 · `ModelContextProtocol` NuGet 1.3.0 · xUnit · Docker

## TL;DR — three ways to run it

```bash
# 1. Local .NET (fastest if you have .NET 10)
set -a; source configs/lhr.env; set +a
dotnet run --project src/AtcMcpServer

# 2. Docker (no .NET install needed)
docker build -t atc-mcp .
docker run --rm -i --env-file configs/lhr.env atc-mcp

# 3. Pre-built test suite (proves everything works before you wire to Claude)
dotnet test
```

## Submission artifacts (quick links)

| What | Where |
|------|-------|
| **Source code** | [`src/AtcMcpServer/`](./src/AtcMcpServer/) — single .NET 10 console project, no extra services |
| **Tests** | [`tests/AtcMcpServer.Tests/`](./tests/AtcMcpServer.Tests/) — 19 tests including all 3 brief scenarios |
| **Scheduling write-up** | [`report.md`](./report.md) — approach, decisions, what worked, what didn't |
| **Env reference** | [`.env.example`](./.env.example) — every variable documented with examples |
| **Tools + resources reference** | [§ Reference](#reference-tools--resources) below |

## What this server is

A **single .NET 10 process** that speaks MCP over stdio. Clients connect, list tools/resources, and use them. There is no database (state lives in process memory) and no network exposure unless the client opens a stdio pipe to the binary.

You can:

- **Submit flights** with priority, dependencies on other flights, and optional runway capability requirements.
- **Generate a schedule** that respects every constraint the brief specifies.
- **Get status** — a structured snapshot for AI clients to reason on.
- **Cancel flights** — and have all dependants re-evaluated automatically.
- **Find the bottleneck** — the critical chain that determines total schedule duration, returned both as structured JSON and as a renderable Mermaid Gantt diagram.

---

## Install & build

### Prerequisites

- **.NET 10 SDK** (`dotnet --version` should report `10.0.x`). Get it from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- Optional: **Node 18+** if you want to use `@modelcontextprotocol/inspector` for visual debugging.

### Clone + build + test

```bash
git clone https://github.com/Warhammer2000/Task-4.git
cd Task-4/task-4

# Build everything
dotnet build

# Run the test suite (19 tests, includes all 3 brief scenarios)
dotnet test
```

You should see `Пройден! / Passed! : 0 failed, 20 passed`.

---

## Configuration (environment variables)

All limits are loaded from environment variables. The server validates them at startup and exits with code 78 (`EX_CONFIG`) with a precise error message if any value is missing or invalid.

| Variable | Type | Example | Description |
|----------|------|---------|-------------|
| `ATC_RUNWAYS` | csv | `R1:3500:medium,R2:4000:heavy` | Comma-separated list of `id:length_meters:category` triples. Defines every runway in the airport. At least one required. |
| `ATC_RUNWAY_COUNT` | int (optional) | `2` | If set, must equal the number of `ATC_RUNWAYS` entries — otherwise the server fails to start. |
| `ATC_GATE_COUNT` | int ≥ 1 | `4` | Number of gates. Gate ids `G1..GN` are auto-generated. |
| `ATC_GROUND_CREW_COUNT` | int ≥ 1 | `4` | Maximum number of simultaneous in-progress operations (capacity constraint). |
| `ATC_SEPARATION_BUFFER_TAKEOFF_S` | int ≥ 0 | `60` | Minimum gap on the same runway between two consecutive **takeoffs**, in seconds. |
| `ATC_SEPARATION_BUFFER_LANDING_S` | int ≥ 0 | `90` | Minimum gap on the same runway between two consecutive **landings**, in seconds. |
| `ATC_SEPARATION_BUFFER_MIXED_S` | int ≥ 0 | `120` | Minimum gap on the same runway when consecutive ops are of **different** operation types, in seconds. |
| `ATC_GATE_TURNAROUND_TIME_S` | int ≥ 0 | `600` | Gate-occupancy turnaround buffer after a flight releases a gate. |
| `ATC_DEPENDENCY_BUFFER_S` | int ≥ 0 | `300` | Minimum gap between a dependency's end time and its dependant's start. |
| `ATC_OP_DURATION_ARRIVAL_S` | int ≥ 1 | `600` | Default arrival operation duration (seconds). |
| `ATC_OP_DURATION_DEPARTURE_S` | int ≥ 1 | `480` | Default departure operation duration (seconds). |
| `ATC_SCHEDULING_HORIZON_HOURS` | int 1–168 | `12` | Maximum scheduling horizon in hours from the moment `generate_schedule` is called. |

Copy [`.env.example`](./.env.example) → `.env`, adjust, and source it before running the server (or pass values inline as shown below).

---

## Run the server

The server speaks MCP over stdio — it doesn't open a network port. A client (Claude Desktop, mcp-inspector, or a custom MCP host) launches it as a subprocess.

### Quick local run (smoke test)

```bash
cd task-4

ATC_RUNWAYS="R1:3500:medium,R2:4000:heavy" \
ATC_GATE_COUNT=4 \
ATC_GROUND_CREW_COUNT=4 \
ATC_SEPARATION_BUFFER_TAKEOFF_S=60 \
ATC_SEPARATION_BUFFER_LANDING_S=90 \
ATC_SEPARATION_BUFFER_MIXED_S=120 \
ATC_GATE_TURNAROUND_TIME_S=600 \
ATC_DEPENDENCY_BUFFER_S=300 \
ATC_OP_DURATION_ARRIVAL_S=600 \
ATC_OP_DURATION_DEPARTURE_S=480 \
ATC_SCHEDULING_HORIZON_HOURS=12 \
dotnet run --project src/AtcMcpServer
```

The server prints structured logs to **stderr** (stdout is reserved for the MCP JSON-RPC protocol). On stdin-EOF it shuts down cleanly.

### Run via Docker (no .NET install needed)

```bash
docker build -t atc-mcp .

# Pick a real-airport config from configs/ — or use your own .env
docker run --rm -i --init --env-file configs/lhr.env atc-mcp
```

The image is ~300 MB (Microsoft's official .NET 10 runtime base + the published DLLs). The container runs as non-root (`app` user, uid 1654). Stdio is the transport so **`-i` is required**; there's no port to expose.

**`--init` is also required**: it makes tini PID 1 inside the container, which forwards signals correctly and ensures dotnet's stdout flushes before process exit on stdin EOF. Without it, the final JSON-RPC response is silently lost during teardown. (Discovered the hard way — see `report.md` §5 *"what didn't work"*.)

### Pre-built airport configurations

Four real airports are pre-configured in [`configs/`](./configs/). Just point at the one you want:

| File | Airport | Runways | Gates | Notable |
|------|---------|---------|-------|---------|
| [`configs/lhr.env`](./configs/lhr.env) | London Heathrow | 2 (3902m heavy, 3660m medium) | 8 | ICAO Cat-F dimensions |
| [`configs/jfk.env`](./configs/jfk.env) | New York JFK | 4 (mixed lengths/categories) | 12 | Multi-runway capability matching |
| [`configs/dxb.env`](./configs/dxb.env) | Dubai International | 2 (both 4000m+ heavy) | 10 | Wake-turbulence separation |
| [`configs/ala.env`](./configs/ala.env) | Almaty | 2 (3400m medium, 4500m heavy) | 6 | Constrained gate capacity |

See [`configs/README.md`](./configs/README.md) for sourcing details.

### Connect from Claude Desktop

Add this block to your `claude_desktop_config.json` (Settings → Developer → Edit Config).

**Option A — local dotnet:**

```json
{
  "mcpServers": {
    "atc": {
      "command": "dotnet",
      "args": [
        "run", "--project",
        "C:/absolute/path/to/Task-4/task-4/src/AtcMcpServer",
        "--no-build"
      ],
      "env": {
        "ATC_RUNWAYS": "R1:3500:medium,R2:4000:heavy",
        "ATC_GATE_COUNT": "4",
        "ATC_GROUND_CREW_COUNT": "4",
        "ATC_SEPARATION_BUFFER_TAKEOFF_S": "60",
        "ATC_SEPARATION_BUFFER_LANDING_S": "90",
        "ATC_SEPARATION_BUFFER_MIXED_S": "120",
        "ATC_GATE_TURNAROUND_TIME_S": "600",
        "ATC_DEPENDENCY_BUFFER_S": "300",
        "ATC_OP_DURATION_ARRIVAL_S": "600",
        "ATC_OP_DURATION_DEPARTURE_S": "480",
        "ATC_SCHEDULING_HORIZON_HOURS": "12"
      }
    }
  }
}
```

**Option B — Docker (after `docker build -t atc-mcp .`):**

```json
{
  "mcpServers": {
    "atc": {
      "command": "docker",
      "args": [
        "run", "--rm", "-i", "--init",
        "--env-file", "C:/absolute/path/to/configs/lhr.env",
        "atc-mcp:latest"
      ]
    }
  }
}
```

Then **restart Claude Desktop**. The 5 tools and 3 resources show up under the airport icon in the chat composer. Ask Claude: *"Submit a high-priority arrival BA101 and a connecting departure LH202 that depends on it, then generate the schedule and tell me the bottleneck."*

### Connect from `@modelcontextprotocol/inspector` (visual debugger)

```bash
# Build first so the inspector launches a hot binary, not dotnet-run
dotnet build src/AtcMcpServer -c Release

# Launch the inspector against the compiled DLL
npx @modelcontextprotocol/inspector \
  dotnet src/AtcMcpServer/bin/Release/net10.0/AtcMcpServer.dll
```

The inspector opens at `http://localhost:5173` with a UI to list/call tools, list/read resources, and inspect the raw JSON-RPC traffic.

### Continuous integration

Every push to `main` (and every PR) triggers [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) which:

1. Builds the .NET 10 solution in Release mode.
2. Runs the full 19-test xUnit suite.
3. Boots the MCP server with a real airport config (`configs/lhr.env`) and sends `initialize` + `tools/list` + `resources/list` over stdio — asserts all 5 tools and all 3 resources appear in the response.
4. Builds the Docker image and repeats the stdio smoke inside the container with `--env-file configs/lhr.env`.

The badge at the top of this README links to the latest run. Green = everything compiles, all tests pass, and both bare-metal and Docker runtimes serve the MCP protocol correctly.

---

## Reference: tools & resources

### Tools (5)

| Name | Description |
|------|-------------|
| **`submit_flight`** | Submit a new flight to the pending queue. Parameters: `flightNumber` (string), `operationType` (`"arrival"` or `"departure"`), `priority` (`"high"` / `"medium"` / `"low"`), `dependsOn` (string[], optional — flight numbers to wait on), `minRunwayLengthMeters` (int, optional), `requiredRunwayCategory` (string, optional). Fails fast on duplicate flight numbers or unknown dependency targets. |
| **`generate_schedule`** | Recompute the schedule from scratch using the current queue and configuration. Replaces any prior schedule. Returns the full result: scheduled slots (flight number, runway, gate, start/end times), unscheduled flights with structured reason codes, and the schedule completion time. |
| **`airport_status`** | Structured operational snapshot: flight counts by status (`Queued`/`Scheduled`/`Unscheduled`/`Cancelled`) and by operation type, runway/gate capacity and usage, ground-crew capacity, resource constraint indicators (`runway_capacity`, `dependency_blockers`), unscheduled flights with reasons, and the current schedule completion time when available. |
| **`cancel_flight`** | Mark a flight as cancelled. Dependant flights are flipped back to the queue for re-evaluation; the next `generate_schedule` call will surface them as Unscheduled with `DEPENDENCY_CANCELLED` reason. Idempotent — returns `false` for unknown or already-cancelled flights. |
| **`bottleneck_analysis`** | Identify the longest active scheduled dependency chain. Returns the ordered flights, their slot details, total elapsed duration (operation durations + dependency buffers), and a renderable Mermaid Gantt diagram of the chain — paste-friendly in any markdown surface. |

### Resources (3)

| URI | Name | Description |
|-----|------|-------------|
| **`atc://flights/queue`** | `flight_queue` | All submitted flights with status (Queued, Scheduled, Unscheduled, Cancelled), dependencies, runway requirements, and unschedule reasons when applicable. The canonical "what is in the system right now" view. |
| **`atc://runways`** | `runways` | Configured runways with their capabilities (length, category), the separation buffers in effect, and the slots already scheduled on each runway. Useful for spotting runway-bound bottlenecks. |
| **`atc://timeline`** | `operation_timeline` | Chronological timeline of every scheduled operation, sorted ascending by start time. The "render this schedule" view. |

---

## Unschedule reason codes

When a flight ends up Unscheduled, its `reasonCode` is one of:

| Code | When |
|------|------|
| `NO_SUITABLE_RUNWAY` | No configured runway meets the flight's `minRunwayLengthMeters` and/or `requiredRunwayCategory`. (Brief Scenario 2.) |
| `DEPENDENCY_MISSING` | Flight depends on an unknown flight number. Submission catches most of these, but a dynamic case (cancel + resubmit reordering) can surface this here. |
| `DEPENDENCY_CANCELLED` | A dependency was cancelled via `cancel_flight`. |
| `DEPENDENCY_UNSCHEDULED` | A dependency itself ended up Unscheduled for some other reason — this flight transitively cannot fly. |
| `DEPENDENCY_CYCLE` | Two or more flights form a dependency cycle (detected at scheduler entry). |
| `NO_RESOURCE_SLOT_IN_HORIZON` | The flight has runway capability but every (runway, gate) combination would push its end past the scheduling horizon. |

Human-readable detail is in `reasonMessage` alongside the code.

---

## Project layout

```
task-4/
├── README.md                            ← this file
├── report.md                            ← scheduling approach, decisions, what worked/didn't
├── .env.example                         ← every env var, documented
├── .gitignore
├── src/
│   └── AtcMcpServer/
│       ├── AtcMcpServer.csproj          ← .NET 10, ModelContextProtocol 1.3.0
│       ├── Program.cs                   ← entry point + MCP host bootstrap
│       ├── Configuration/
│       │   ├── AirportConfig.cs         ← typed config record + ConfigurationException
│       │   └── AirportConfigLoader.cs   ← env → AirportConfig with fail-fast validation
│       ├── Domain/
│       │   ├── Primitives.cs            ← enums (OperationType, Priority, FlightStatus, UnscheduleReasons)
│       │   ├── Flight.cs, Runway.cs, ScheduleEntry.cs, ScheduleResult.cs
│       ├── Scheduling/
│       │   ├── Scheduler.cs             ← greedy + dependency-aware constraint solver
│       │   └── BottleneckAnalyzer.cs    ← DAG longest-path + Mermaid Gantt render
│       ├── State/
│       │   └── AirportState.cs          ← single in-memory source of truth, thread-safe
│       └── Mcp/
│           ├── AtcTools.cs              ← 5 tools, attribute-discovered
│           └── AtcResources.cs          ← 3 resources, attribute-discovered
└── tests/
    └── AtcMcpServer.Tests/
        ├── BriefScenarioTests.cs        ← Scenarios 1/2/3 from the brief
        ├── DeterminismTests.cs          ← R11: same input → same output
        ├── BottleneckTests.cs           ← R10: longest dependency chain + duration math
        ├── ConfigValidationTests.cs     ← R2b: every env var fails clearly when bad
        ├── CancellationTests.cs         ← R8: cancel marks + re-evaluates dependants
        └── TestHelpers.cs               ← shared builders, fixed scheduling instant
```

---

## License & attribution

This is a personal portfolio submission for Vention's AI Challenge 2.0. Code is provided as-is. The MCP protocol and `ModelContextProtocol` NuGet package are MIT-licensed work by Anthropic.
