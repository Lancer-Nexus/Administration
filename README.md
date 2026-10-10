# Lancer Nexus Administration

The Administration service provides one authorized command system for external REST clients and in-game administrator chat commands.

The full data model, evaluation order, scopes, API routes, migration/bootstrap steps and current rollout limits are documented in [docs/permission-system.md](docs/permission-system.md).

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

The implemented service exposes `/health/live`, `/health/ready` and scoped status queries through `/api/v1/admin/commands`. It uses the Coordinator registry and SQL authorization/audit. Separate `/api/v1/admin/status/*` views, detailed host/service/Gateway/Redis metrics and player statistics remain planned. Stale registry data is explicitly reported.

The service must not trust a client-provided admin flag or a shared chat password. It uses Gateway-authenticated identity and current SQL authorization for every mutating command.

Typed game actions are accepted internally at `POST /api/v1/admin/actions` with the Gateway service credential. Gateway forwards REST and in-game requests after validating the current session and, for player targets, resolving current lease and Coordinator placement context. Administration rechecks the current permission snapshot and target scopes, then atomically writes an idempotent pending command, JSON outbox item and decision audit. A `202 Pending` response means queued; it does not mean a game server executed the action. Gateway does not yet provide signed step-up evidence, so permanent bans and instance drain remain denied. The Coordinator/Agent/LLServer outbox consumer and execution-result path remain integration work.

## Implemented administration queries

`/admin help`, `/admin status`, `/admin instances` and `/admin instance <ID>` are read-only commands. LLServer intercepts complete chat text before ordinary logging/broadcast and returns private console messages. Gateway checks the instance credential, admitted account/session and active character lease. Following a transfer it resolves the admitted transfer's session through Coordinator and checks the current lease again. Local game administrator flags and character names never grant rights.

External clients send Protocol `AdminQuery` JSON to Gateway `POST /api/v1/admin/commands` with their Gateway access token. Gateway checks the current SQL session and attests identity over authenticated HTTPS. Both transports call `AdminQueryDispatcher`. Administration's endpoint with the same path is internal and accepts only the separate Gateway service credential; do not expose it publicly.

Apply [sql/001_admin_queries.sql](sql/001_admin_queries.sql), [sql/002_permissions.sql](sql/002_permissions.sql), [sql/003_command_operator_group.sql](sql/003_command_operator_group.sql), [sql/004_permission_audit_decisions.sql](sql/004_permission_audit_decisions.sql), [sql/005_admin_action_outbox.sql](sql/005_admin_action_outbox.sql), and [sql/006_clusterjump_operator_permission.sql](sql/006_clusterjump_operator_permission.sql) explicitly in order against the shared Gateway/Administration MySQL database. The permission migrations create the normalized graph, revision outbox, per-instance ACKs, audit and idempotency tables. They import enabled legacy admin role assignments and permissions, then give all existing accounts the empty `default` group. Migration 004 adds explicit decision, reason and result fields to mutation audit records. Migration 005 adds pending typed-action commands, a transactional JSON outbox, and decision audit rows. Migration 006 adds the cluster-jump grant to the unassigned operator group. The action outbox from migration 005 retains payloads such as broadcast text until a consumer completes or removes them. Legacy tables remain available for rollback; the service authorizes through the new permission graph.

The isolated local E2E database migration and idempotence check are recorded in [docs/permissions-testdb.md](docs/permissions-testdb.md).

The migration deliberately creates no privileged account. For initial bootstrap, choose the owner account UUID from `accounts` and add a single global `permissions.manage` exact allow node at the next free user-node position. Keep this operation in the controlled database deployment record:

```sql
SET @owner_account = '00000000-0000-0000-0000-000000000000';
SELECT COALESCE(MAX(position) + 1, 0) INTO @owner_node_position
FROM permission_user_nodes WHERE account_id = @owner_account;
INSERT INTO permission_user_nodes
    (account_id, position, permission_pattern, pattern_kind, allowed)
SELECT @owner_account, @owner_node_position, 'permissions.manage', 0, TRUE
WHERE EXISTS (SELECT 1 FROM accounts WHERE account_id = @owner_account)
  AND NOT EXISTS (SELECT 1 FROM permission_user_nodes
      WHERE account_id = @owner_account AND permission_pattern = 'permissions.manage'
        AND system_id IS NULL AND instance_id IS NULL);
```

Thereafter, group/user changes go through the authenticated mutation API. A test account should receive only its intended group and scope; do not grant `permissions.manage` just to run status queries.

Administration and Gateway require a distinct shared Gateway service key; Administration also requires `Administration__RedisEndpoint=127.0.0.1:6379` (set the actual private endpoint for the deployment). The SQL outbox publishes revision notices to `lancer-nexus:permissions:revision`. The Cluster library supplies the snapshot source, Redis listener and ACK coordinator. The current LibreLancer patch series has not yet started these Cluster components inside LLServer, so running game instances will not ACK until that host wiring is added; permission changes currently remain durable as `202 sync_pending`. Once wired, the expected behavior is `200` only after all currently active Coordinator instances ACK within five seconds, while offline instances load the current SQL snapshot before serving. Redis is notification transport, never permission authority. The RESP adapter currently expects a trusted Redis TCP endpoint without Redis AUTH or TLS, so isolate it on the private network.

The shared C# evaluator lives in the Cluster project. Administration references its evaluator; Cluster consumers should use `IPermissionService` rather than implementing their own group, inheritance, context, expiry, prefix or rank rules. Since the service repositories are independently versioned, publish and pin Cluster as a package before consuming this project reference outside the combined development workspace.

Existing status-query accounts use the normal node model: grant the needed `admin.help`, `status.read` and `instance.read` rights through a scoped group, then assign that group. Use a dedicated SQL user with access to the permission tables, ACK/outbox tables, audit and idempotency rows; keep audit append-only for routine service operations.

Each request, including retries, reads current rights. Status is withheld if durable audit fails. Account-scoped idempotency keys bind to command kind and target; reuse for another command returns 409. Repeated reads recompute fresh scoped data and append new audit events. Audit stores account/correlation/source/command/target/outcome, never credentials, raw chat or result payloads. Missing sources fail closed.

Registry results omit private endpoints and account identities. Heartbeats older than 15 seconds, future timestamps or offline Agents prevent readiness. Multi-system capacity is counted once. Connection counts are distinct from unique online players; online presence remains unknown. Responses include timestamps and no-store headers. Gateway limits admin queries to 60/minute/source IP; LLServer allows one outstanding query/player with a two-second cooldown. Kick, ban, drain, move and teleport workflows remain unimplemented and are rejected.

### Configuration

Keep actual secrets in private environment files:

```text
ConnectionStrings__Administration=<private MySQL connection string>
Administration__GatewayApiKey=<distinct service secret of at least 32 characters>
Administration__CoordinatorBaseUrl=https://localhost:38444
Administration__CoordinatorApiKey=<Coordinator internal service secret>
ASPNETCORE_URLS=https://localhost:38445
ASPNETCORE_Kestrel__Certificates__Default__Path=<private server PFX>
ASPNETCORE_Kestrel__Certificates__Default__Password=<private PFX password>
SSL_CERT_FILE=<trusted internal CA PEM>
```

Gateway needs `Gateway__AdministrationBaseUrl=https://localhost:38445` and `Gateway__AdministrationApiKey` matching `Administration__GatewayApiKey`. LLServer uses its existing LoginUrl and distinct instance key; standalone servers cannot query Administration. Never expose Administration's service-key endpoints directly to a browser or game client.

Initialize the Protocol submodule before building. `/health/live` proves liveness. `/health/ready` checks SQL schema (including migration 005), service key configuration and Coordinator registry, returning 503 if missing. It does not prove game instance readiness or audit write permissions; commands check authorization and actual audit writes independently.

Build from the repository root with:

```bash
dotnet restore LancerNexus.Administration.sln
dotnet build LancerNexus.Administration.sln --configuration Release --no-restore
dotnet test LancerNexus.Administration.sln --no-restore
```

CI runs the permission mutation integration test against an ephemeral MySQL 8 service. To run it locally, set `LANCER_NEXUS_ADMINISTRATION_TEST_MYSQL` to a disposable MySQL connection string with permission to create and drop databases; the test applies both Administration migrations to a uniquely named database and removes it afterward. Without that variable, xUnit reports the integration test as skipped.
