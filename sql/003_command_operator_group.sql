-- Apply after 002_permissions.sql. Seeds the explicit OP role once; no account is assigned.
CREATE TABLE IF NOT EXISTS permission_seed_migrations (
    seed_name VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    applied_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))
) ENGINE=InnoDB;

START TRANSACTION;
INSERT IGNORE INTO permission_seed_migrations(seed_name) VALUES ('003_command_operator_group_v1');
SET @command_operator_seeded = ROW_COUNT();

INSERT IGNORE INTO permission_groups(group_name, rank_value, ladder, group_prefix)
SELECT 'operator', 100, 'staff', '[Operator]' WHERE @command_operator_seeded = 1;

INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 0, 'command.addcash', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 1, 'command.base', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 2, 'command.damageself', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 3, 'command.godmode', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 4, 'command.npc', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 5, 'command.setlevel', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 6, 'command.spawnloot', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 7, 'command.stop', 0, TRUE WHERE @command_operator_seeded = 1;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 8, 'command.warp', 0, TRUE WHERE @command_operator_seeded = 1;

UPDATE permission_state SET revision = revision + @command_operator_seeded WHERE singleton_id = 1;
INSERT INTO permission_outbox(event_id, revision, created_at_utc)
SELECT UUID(), revision, UTC_TIMESTAMP(6) FROM permission_state
WHERE singleton_id = 1 AND @command_operator_seeded = 1;
COMMIT;
