# Permission migration test database

Validated on 2026-09-28 against the isolated local `lancer_nexus_e2e` database (MariaDB 10.6.23). The connection was read from the protected `runtime/administration/dev/administration.env`; credentials are intentionally not recorded here.

Applied `sql/002_permissions.sql` after the existing Gateway and Administration query schemas. The migration created the normalized permission tables, imported the single enabled test-admin role and its three grants, and added the empty `default` group to the one existing account.

Verified state after migration:

- 2 groups: `cluster-observer` and `default`.
- 3 imported allow nodes: `admin.help`, `instance.read`, and `status.read` on `cluster-observer`.
- 2 memberships: one scoped legacy `cluster-observer` assignment and one global `default` membership.
- After migration 002, permission revision was 1; initial schema migration creates no outbox event or instance ACK.
- Re-running the 17 migration statements left group, node, membership and revision counts unchanged.

Applied `sql/003_command_operator_group.sql` on 2026-09-28. It added the unassigned `operator` group and its nine explicit `command.*` grants for the former LibreLancer admin commands.

Verified state after migration 003:

- `operator` exists at rank 100 on ladder `staff`, with prefix `[Operator]`.
- Exactly 9 command nodes exist on `operator`; no user-group membership was created.
- Permission revision is 2, with one revision-2 outbox row.
- The revision-2 outbox row is marked published, and zero instance ACK rows report revision 2 or later.
- The rebuilt Administration API passes `/health/ready` (HTTP 200) and reads the revision-2 SQL snapshot.

The SQL seed is idempotent and does not grant operator to the test account. This validates schema, legacy import, the command grant seed, snapshot loading and Redis publication. Live LLServer snapshot reload/ACK remains unverified because the current LLServer patch does not yet subscribe to revision notices. The separate per-command Gateway authorization and test login are covered by their service tests and E2E smoke run.
