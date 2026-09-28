# Agent Instructions – Administration

## Authorization

- Administrators are regular Gateway accounts with explicit MySQL admin records, roles and scopes.
- Check current authorization for every mutating command.
- Never authorize from a character name, chat prefix or client-provided flag.
- Reject mutating commands when MySQL authorization state is unavailable.

## Command model

- REST and in-game chat must call the same typed command dispatcher.
- Do not execute arbitrary shell text or SQL from an admin command.
- Require correlation IDs and idempotency keys.
- Audit actor, source, target, decision, reason, result and transfer IDs.

## Transfers

- Resolve player presence through Coordinator.
- Reserve the target before releasing the source.
- Use short-lived JoinTickets and prevent duplicate ownership.
- Reconcile or time out incomplete transfers safely.

## Security

Never log passwords, tokens, JoinTickets or sensitive command payloads. Apply rate limits, explicit scopes and confirmation/step-up rules for permanent bans, formation movement and instance draining.

## Build

Initialize the Protocol submodule. AdminQuery wire contracts belong in Protocol. Gateway attests current session/lease identity; Administration checks its separate Gateway service credential, normalized SQL permissions/scopes and durable audit. Retries must recompute current rights and fresh scoped status. Never cache authorized status across revocation. Supported queries are help/status/instances/instance. Permission mutations use the closed typed mutation schema and return pending until active instances acknowledge the committed revision. Report stale/unknown data, count capacity once per multi-system instance and keep connection counts distinct from online accounts.

Use `LancerNexus.Administration.sln` with .NET 10. Keep readiness fail-closed until authorization, revision outbox and audit dependencies are active; do not add a permissive development admin path.

The normalized SQL permission graph and shared evaluator replace legacy role authorization after migration 002. All mutations require `permissions.manage`, run in a transaction, validate the complete candidate inheritance graph, append audit/idempotency/outbox records, and publish a revision. Do not allow direct public access to Administration's service-key endpoints. Redis notices only trigger a full SQL snapshot reload; instance ACKs follow activation.

Use [docs/permission-system.md](docs/permission-system.md) as the detailed reference for evaluation order, API boundaries, migrations, operations and known LLServer sync limitations. Keep it aligned whenever those contracts change.
