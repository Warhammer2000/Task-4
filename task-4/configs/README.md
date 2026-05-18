# Real-airport configurations

Each `.env` file in this directory is a drop-in, real-airport-accurate
config for the MCP server. Pick one to load:

```bash
# Linux / macOS / WSL — source the env into your shell
set -a; source configs/lhr.env; set +a
dotnet run --project src/AtcMcpServer
```

```powershell
# Windows PowerShell
Get-Content configs/lhr.env | ForEach-Object {
    if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
        $env:($matches[1].Trim()) = $matches[2].Trim()
    }
}
dotnet run --project src/AtcMcpServer
```

For Docker — see [`docker-compose.yml`](../docker-compose.yml); the
`env_file` directive picks up these files directly:

```bash
ATC_CONFIG=configs/lhr.env docker compose up
```

## Available configurations

| File | Airport | Runways | Gates | Note |
|------|---------|---------|-------|------|
| [`lhr.env`](./lhr.env) | London Heathrow (EGLL) | 2 (27L 3902m heavy, 27R 3660m medium) | 8 | Real ICAO Cat-F dimensions; demonstrates capability-based routing between the two runways. |
| [`jfk.env`](./jfk.env) | New York JFK (KJFK) | 4 (mix of medium and heavy) | 12 | JFK's four-runway layout used aggressively to exercise the scheduler's multi-runway capability matching. |
| [`dxb.env`](./dxb.env) | Dubai International (OMDB) | 2 (both heavy, 4000m+) | 10 | Both runways A380-rated; demonstrates the case where capability filter is non-restrictive. |
| [`ala.env`](./ala.env) | Almaty International (UAAA) | 2 (3400m medium, 4500m heavy) | 6 | Regional hub with constrained gate capacity — good for testing resource contention. |

Sources for runway dimensions and category data: Wikipedia airport pages
(cross-checked against official airport authority publications).
