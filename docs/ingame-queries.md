# Ingame administration: implementation and activation

## Implemented path

1. LLServer reconstructs fragmented chat and intercepts `/admin` before ordinary broadcast/logging.
2. The closed Protocol parser creates a bounded read-only query with correlation and idempotency identifiers.
3. LLServer adds its admitted Gateway identity and sends the query with its distinct instance credential.
4. Gateway validates the active account/session/character lease for the authenticated instance. Transfer admission resolves the committed transfer's session, then still validates the current lease.
5. Administration authenticates Gateway, reads current SQL roles and instance scopes, obtains fresh Coordinator data and filters it.
6. Durable audit succeeds before the result returns through Gateway and the caller's private console RPC.

Commands: `/admin help`, `/admin status`, `/admin instances`, `/admin instance li-01` (or the actual registered ID).

Status includes instance ID, owned worlds, readiness/draining/stale state, connection counts, reservations, capacity and heartbeat timestamp. Capacity is shared across all worlds owned by one instance. Offline prepared configurations are not fabricated as live registry entries. The old E2E IDs `li01`/`li02` differ from the eight-group baseline IDs `li-01`/etc.

## Local development preparation

The explicitly authorized existing Gateway test account has `cluster-observer` with scope `*` in the isolated E2E MySQL database. No other account was granted rights. The Administration schema was applied there; general deployments must apply it explicitly and provision their own existing Gateway account.

Private local settings and an executable starter are under `runtime/administration/dev/` outside generated build outputs. The private starter loads its protected environment and launches `output/dev/administration/api/LancerNexus.Administration.Api.dll`. It trusts the current E2E TLS certificate and uses a distinct Gateway service secret. These temporary E2E certificates/database dependencies must be replaced when deploying independently.

Administration listens on `https://localhost:38445`. The E2E Gateway environment has Administration relay settings for its next restart. Updated Gateway/Administration builds and LLServer/client engine dependencies are staged in Debug output. Running Gateway/game/client processes must restart to load the new implementation. No existing player lease or character state was remapped.

## Verification

- Protocol tests cover parser bounds, closed command kinds and explicit MessagePack round trips.
- Administration tests cover denied queries without source reads, exact instance scopes, revocation on retry, multi-system capacity, stale/future heartbeats, unavailable sources, idempotency conflict and withheld data on audit failure.
- Gateway tests verify active lease/instance binding, checked identity forwarding and response correlation.
- Engine tests exercise fragmented admin chat in local/system categories and private feedback without entering world chat.
- A real HTTPS/MySQL smoke exercised service authentication, SQL roles/scopes, no-store summaries, key conflicts, revocation and seven durable audits in a disposable database; the process/database were removed afterward.
- A GUI test entering these commands in a connected client is still pending the process restart. The engine and LLServer Debug build succeeded, with existing upstream warnings.

## Remaining administration work

Mutating actions (kick/message/ban/drain/move/teleport), player presence/statistics, service/host status views, confirmation/step-up and QUIC administration contracts remain open. They must retain current SQL authorization, typed dispatch, durable audit, transfer fencing and actual operation acknowledgements. Never route them through the local LibreLancer administrator flag.
