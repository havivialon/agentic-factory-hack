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

## Status
Steps 0 (scaffold), 1 (data and seeding) and 2 (fake Waterstoring.nl) done. Next: the C# MCP functions.
