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

Use `LancerNexus.Administration.sln` with .NET 10. Keep readiness fail-closed until authorization and audit dependencies are active; do not add a permissive development admin path.
