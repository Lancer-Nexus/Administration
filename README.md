# Lancer Nexus Administration

The Administration service provides one authorized command system for external REST clients and in-game administrator chat commands.

## Responsibilities

- Resolve administrators and roles from MySQL
- Enforce permissions and server/instance scopes
- Expose an OpenAPI REST API
- Dispatch the same typed commands used by in-game `/admin` chat
- Resolve global player presence through Coordinator
- Execute kick, ban, message, move, teleport, drain and transfer workflows
- Record append-only audit events
- Expose authorized cluster, service, host, agent and instance status endpoints
- Expose registered-account, character, online-player and player-statistics summaries

Player counts distinguish accounts, characters and active sessions. Online counts come from fresh Coordinator presence leases; Redis is only an acceleration/cache layer. The current LibreLancer statistics are imported as mission, visit and ship-class kill counters. PvP, deaths, assists and damage require the optional authoritative server event sink described in the cluster plan.

## Status API

The service exposes `/health/live`, `/health/ready` and scoped administrative endpoints under `/api/v1/admin/status`. Status aggregation uses Coordinator and Agent heartbeats, LLServer runtime snapshots, Gateway metrics, MySQL readiness and Redis health. Stale data is reported explicitly and stale instances are excluded from placement.

The service must not trust a client-provided admin flag or a shared chat password. It uses Gateway-authenticated identity and current SQL authorization for every mutating command.

## Current skeleton

The .NET 10 ASP.NET Core host exposes `/health/live` and fails readiness closed at `/health/ready` until authorization, audit storage and command dispatch are wired. The shared typed command envelope and closed initial command types are in place; the REST route remains unavailable until identity, authorization and audit services are registered.

Build from the repository root with:

```bash
dotnet restore LancerNexus.Administration.sln
dotnet build LancerNexus.Administration.sln --configuration Release --no-restore
```
