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

## Build
```powershell
dotnet build evides-leakops\LeakOps.slnx
```

## Status
Step 0 (scaffold) done. Next: simulated data and Cosmos seeding.
