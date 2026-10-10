-- Apply after 005_admin_action_outbox.sql. Adds the test transfer command to
-- the existing operator group without assigning that group to any account.
CREATE TABLE IF NOT EXISTS permission_seed_migrations (
    seed_name VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    applied_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))
) ENGINE=InnoDB;

START TRANSACTION;
INSERT IGNORE INTO permission_seed_migrations(seed_name)
VALUES ('006_clusterjump_operator_permission_v1');
SET @clusterjump_seeded = ROW_COUNT();

INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT 'operator', 9, 'command.clusterjump', 0, TRUE WHERE @clusterjump_seeded = 1;

UPDATE permission_state SET revision = revision + @clusterjump_seeded WHERE singleton_id = 1;
INSERT INTO permission_outbox(event_id, revision, created_at_utc)
SELECT UUID(), revision, UTC_TIMESTAMP(6) FROM permission_state
WHERE singleton_id = 1 AND @clusterjump_seeded = 1;
COMMIT;
