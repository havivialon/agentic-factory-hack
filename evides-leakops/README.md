# Evides LeakOps

Multi-agent leak response for a drinking-water network, built in C# on Azure Functions. Six agents take a pressure/flow
anomaly from "is this a real leak?" to "technician dispatched and residents informed on Waterstoring".
All data is **simulated**. The Waterstoring site in this folder is a **fake**, not the real waterstoring.nl.

| # | Agent | Job |
|---|---|---|
| 1 | Triage | SCADA/IoT + planned works + weather + usage history: genuine leak or false alarm |
| 2 | Diagnostic | GIS: exact location, pipe material and diameter, severity |
| 3 | Communications | Public text, pushed to the (fake) Waterstoring ingest API |
| 4 | Resource Matchmaker | Closest available technician with the right certification |
| 5 | Procurement | Parts and equipment reservation, purchase order if stock is low |
| 6 | Scheduling | Travel and repair time, work order, ETA update on Waterstoring |

## Layout
| Path | Purpose |
|---|---|
| `src/LeakOps.Contracts` | Shared records |
| `src/LeakOps.Seeder` | Seeds Cosmos DB `LeakOpsDB` |
| `src/LeakOps.Mcp` | Azure Functions app exposing the MCP tools |
| `src/LeakOps.Agents` | Azure Functions app with the six agent HTTP functions |
| `src/LeakOps.Waterstoring` | Fake Waterstoring.nl (ingest API + public map) |
| `src/LeakOps.Workflow` | ASP.NET workflow host (sequential pipeline, SSE, trigger page) |
| `src/LeakOps.AppHost` | Aspire AppHost for local runs |
| `tests/LeakOps.Tests` | Unit tests |

## Local ports
MCP functions `7071` · Agent functions `7072` · Waterstoring `5080` · Workflow `5090`

## Prerequisites
.NET 10 SDK, Azure Functions Core Tools v4, Azurite (local storage emulator), Azure CLI (`az login`).

## Local settings
Copy `local.settings.sample.json` to `local.settings.json` in `src/LeakOps.Mcp` and `src/LeakOps.Agents` (git-ignored) and fill in the values.
See `env.example` for the variable names.

## Build and test
```powershell
dotnet build evides-leakops\LeakOps.slnx
dotnet test --project evides-leakops\tests\LeakOps.Tests
```
(`global.json` opts in to the Microsoft.Testing.Platform runner that xunit v3 needs on the .NET 10 SDK.)

## Simulated data and seeding
Static data lives in `data/*.json`. Sensor readings (10-minute interval), weather and usage profiles are **generated
deterministically** from `data/simulation.json` when the seeder runs. All times are UTC (the Netherlands is UTC+1 in November).

Two scripted scenarios (`data/scenarios.json`; the `groundTruth` part is for tests and evaluation only, agents never see it):

| Scenario | District | Alarm time (UTC) | What the data shows |
|---|---|---|---|
| `scn-burst-cast-iron` | D-01 Spijkenisse-Noord | 2026-11-14 03:10 | PS-101 drops to 2.85 bar, night flow triples, -3 C frost, no planned works. Burst on SEG-0412 (1962 cast iron DN300, about 160 m from its start) |
| `scn-false-alarm-planned-works` | D-02 Hoogvliet-Industrie | 2026-11-13 09:40 | PS-201 drops to 3.72 bar while inflow goes down. Planned work PW-7781 (valve replacement) is in progress |

No sensor data exists after the alarm time, so agents cannot see the future. The roster is built so that the nearest on-shift
technician (T-01) lacks the cast-iron certification, and the right answer is T-02.

Seed into Cosmos DB (new database `LeakOpsDB`, 18 containers; the 4 run-time containers start empty):
```powershell
dotnet run --project evides-leakops\src\LeakOps.Seeder -- --dry-run     # validate and count only
dotnet run --project evides-leakops\src\LeakOps.Seeder                  # upsert (safe to repeat)
dotnet run --project evides-leakops\src\LeakOps.Seeder -- --reset       # drop and recreate LeakOpsDB
dotnet run --project evides-leakops\src\LeakOps.Seeder -- --reset-runtime  # re-seed and empty incidents, work orders, purchase orders, reservations (clean demo state)
```
Uses `COSMOS_ENDPOINT` and `COSMOS_KEY` from the environment or the repo-root `.env`. It deliberately ignores
`COSMOS_DATABASE_NAME` (the `.env` points at the tire-factory database) and refuses any database not named `LeakOps*`.

Notes for querying (used from step 3):
- `value` is a reserved word in Cosmos SQL: write `c["value"]`.
- Timestamps are stored as UTC strings like `2026-11-14T03:10:00+00:00`. Use that exact format in query parameters.

## Fake Waterstoring.nl (`src/LeakOps.Waterstoring`)
A public page where residents look up disruptions, plus an API that the Communications and Scheduling agents call.
Not the real waterstoring.nl: the real site has no known public API, so this contract is our own assumption. Data is kept in
memory (a restart empties it).

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"      # Development falls back to the demo key "dev-waterstoring-key"
dotnet run --project evides-leakops\src\LeakOps.Waterstoring --no-launch-profile --urls http://localhost:5080
```
Open http://localhost:5080 (also served at `/evides`). The page polls every 5 seconds, so new incidents and changed ETAs appear
without a reload. It has a search box (postcode with or without space, street or place), a map and an NL/EN switch. The map and
tiles load from the internet (Leaflet and OpenStreetMap); the list works offline.

| Method and path | Auth | Purpose |
|---|---|---|
| `GET /api/incidents[?active=true]` | public | All incidents, active first |
| `GET /api/incidents/{id}` | public | One incident with its timeline |
| `GET /api/search?q=3201 AB` | public | Active incidents by postcode, street or place (min. 3 characters) |
| `POST /api/incidents` | `X-Api-Key` | Create an incident. Posting the same `incidentId` again replaces it (safe to retry) |
| `PATCH /api/incidents/{id}` | `X-Api-Key` | Change `status`, `message`, `messageEn` and/or `expectedResolutionUtc`; adds a timeline entry |
| `POST /api/admin/reset` | `X-Api-Key` | Clear all incidents (demo reset) |

Statuses: `reported`, `repair_planned`, `repair_in_progress`, `resolved` (resolved incidents leave the search but stay in the list).
Request and response shapes are the records in `src/LeakOps.Contracts/Waterstoring.cs`. Outside Development the app refuses to
start without `WATERSTORING_API_KEY`.

```powershell
curl -X POST http://localhost:5080/api/incidents -H "X-Api-Key: dev-waterstoring-key" -H "Content-Type: application/json" -d '{ ... }'
```

## MCP server (`src/LeakOps.Mcp`)
An Azure Functions app. Each `[McpToolTrigger]` function is an MCP tool at `/runtime/webhooks/mcp` (streamable HTTP; the legacy
SSE endpoint is `/runtime/webhooks/mcp/sse`). Tools read the simulated data in Cosmos DB and write reservations, purchase orders
and work orders. Errors are returned as JSON (`{"error": ...}`) so an agent can read them and correct itself.

| Used by | Tool | What it does |
|---|---|---|
| Triage | `list-sensors` | Sensors of a district with segment, position and alarm limits |
| | `get-sensor-readings` | Readings in a window ending at a time (10-minute interval), with summary |
| | `get-planned-works` | Planned works around a time; `activeAtTime` flag |
| | `get-weather` | Hourly weather with frost hours |
| | `get-usage-history` | What normal looks like for that hour and day type, plus known past deviations |
| Diagnostic | `get-pipe-segment` | GIS data: material, diameter, age, depth, customers, sensors, valves |
| | `get-point-on-segment` | Chainage to coordinates, isolation valves, affected customers and postal codes |
| | `get-nearby-valves` | Valves within a radius of a coordinate |
| | `get-repair-procedure` | Certifications, parts, equipment and hours for a material, diameter and breach type |
| Matchmaker | `get-technicians` | Live roster at a time: on shift, busy, missing certifications, travel time to the site |
| Procurement | `get-inventory` | Stock, reorder levels, prices, warehouses, supplier lead times |
| | `reserve-stock` | Atomic, idempotent reservation (parts and equipment); reports `insufficient_stock`, `equipment_unavailable` |
| | `create-purchase-order` | Order at the supplier, express option, approval above 2500 EUR |
| Scheduling | `get-travel-estimate` | Travel time with optional stop (e.g. depot), depends on time of day |
| | `create-work-order` | Builds the job package, computes arrival and completion, refuses uncertified technicians |

Run it locally (needs Azurite for the Functions host, and `local.settings.json` copied from `local.settings.sample.json`; the
Cosmos key is taken from the environment, so it never has to be written to a file):
```powershell
npx azurite --silent --location $env:TEMP\leakops-azurite        # terminal 1
foreach ($l in Get-Content .env) { if ($l -match '^(COSMOS_ENDPOINT|COSMOS_KEY)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2].Trim().Trim('"'), 'Process') } }
cd evides-leakops\src\LeakOps.Mcp; func start --port 7071           # terminal 2
dotnet run evides-leakops\scripts\mcp-smoke.cs                      # terminal 3: walks the burst scenario through all tools
dotnet run --project evides-leakops\src\LeakOps.Seeder -- --reset-runtime   # clean up what the walk wrote
```
In Azure the endpoint needs the `mcp_extension` system key (header `x-functions-key`) and the app's managed identity needs a
Cosmos DB data role; both are set up in the deployment step. Leave `COSMOS_KEY` empty there and the managed identity is used.

Shared rules (time handling, geometry, travel model, roster check, stock view, purchase math) live in
`src/LeakOps.Contracts/Logic`, so the tools, the agents and the tests use the same code. Local time is a fixed UTC+1.

## Status
Steps 0 to 3 done (scaffold, data and seeding, fake Waterstoring.nl, MCP functions). Next: agent base and the Triage agent.
