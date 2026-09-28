-- Apply after 001_admin_queries.sql. MySQL is authoritative; Redis/control messages only distribute revisions.
CREATE TABLE IF NOT EXISTS permission_state (
    singleton_id TINYINT NOT NULL PRIMARY KEY,
    revision BIGINT NOT NULL DEFAULT 1,
    CONSTRAINT chk_permission_state_singleton CHECK (singleton_id = 1)
) ENGINE=InnoDB;
INSERT IGNORE INTO permission_state(singleton_id, revision) VALUES (1, 1);

CREATE TABLE IF NOT EXISTS permission_groups (
    group_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL PRIMARY KEY,
    rank_value INT NOT NULL DEFAULT 0,
    ladder VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
    group_prefix VARCHAR(128) NULL,
    group_suffix VARCHAR(128) NULL,
    created_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    updated_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_group_inheritance (
    group_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    parent_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    position INT NOT NULL,
    PRIMARY KEY(group_name, parent_name),
    UNIQUE KEY uq_permission_parent_position(group_name, position),
    CONSTRAINT fk_permission_parent_child FOREIGN KEY(group_name) REFERENCES permission_groups(group_name) ON DELETE CASCADE,
    CONSTRAINT fk_permission_parent FOREIGN KEY(parent_name) REFERENCES permission_groups(group_name) ON DELETE CASCADE
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_group_nodes (
    node_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    group_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    position INT NOT NULL,
    permission_pattern VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    pattern_kind TINYINT NOT NULL DEFAULT 1,
    allowed BOOLEAN NOT NULL,
    system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    instance_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    expires_utc DATETIME(6) NULL,
    UNIQUE KEY uq_permission_group_node_position(group_name, position),
    CONSTRAINT fk_permission_group_node FOREIGN KEY(group_name) REFERENCES permission_groups(group_name) ON DELETE CASCADE
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_user_groups (
    account_id CHAR(36) NOT NULL,
    group_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    instance_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    expires_utc DATETIME(6) NULL,
    position INT NOT NULL,
    membership_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    UNIQUE KEY uq_permission_user_group_scope(account_id, group_name, instance_id, system_id),
    KEY ix_permission_user_order(account_id, position),
    CONSTRAINT fk_permission_user_group FOREIGN KEY(group_name) REFERENCES permission_groups(group_name) ON DELETE CASCADE,
    CONSTRAINT fk_permission_user_group_account FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_user_nodes (
    node_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    account_id CHAR(36) NOT NULL,
    position INT NOT NULL,
    permission_pattern VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    pattern_kind TINYINT NOT NULL DEFAULT 1,
    allowed BOOLEAN NOT NULL,
    system_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    instance_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NULL,
    expires_utc DATETIME(6) NULL,
    UNIQUE KEY uq_permission_user_node_position(account_id, position),
    KEY ix_permission_user_nodes(account_id),
    CONSTRAINT fk_permission_user_node_account FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_outbox (
    event_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    revision BIGINT NOT NULL UNIQUE,
    created_at_utc DATETIME(6) NOT NULL,
    published_at_utc DATETIME(6) NULL
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_instance_ack (
    instance_id VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    applied_revision BIGINT NOT NULL DEFAULT 0,
    acknowledged_at_utc DATETIME(6) NULL
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_audit (
    audit_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    created_at_utc DATETIME(6) NOT NULL,
    actor_account_id CHAR(36) NOT NULL,
    correlation_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    source VARCHAR(128) NOT NULL,
    idempotency_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    mutation_kind VARCHAR(32) NOT NULL,
    target VARCHAR(128) NULL,
    revision BIGINT NOT NULL,
    KEY ix_permission_audit_revision(revision)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS permission_mutation_keys (
    actor_account_id CHAR(36) NOT NULL,
    idempotency_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    result VARCHAR(32) NOT NULL,
    revision BIGINT NOT NULL,
    PRIMARY KEY(actor_account_id, idempotency_key)
) ENGINE=InnoDB;

INSERT IGNORE INTO permission_groups(group_name, rank_value, ladder, group_prefix)
VALUES ('default', 0, NULL, NULL), ('cluster-observer', 10, 'staff', '[Observer]');
INSERT IGNORE INTO permission_groups(group_name)
SELECT DISTINCT role_name FROM admin_role_assignments;
INSERT IGNORE INTO permission_groups(group_name)
SELECT DISTINCT role_name FROM admin_role_permissions;
INSERT IGNORE INTO permission_group_nodes(group_name, position, permission_pattern, pattern_kind, allowed)
SELECT role_name, ROW_NUMBER() OVER(PARTITION BY role_name ORDER BY permission), permission, 1, TRUE
FROM admin_role_permissions;
INSERT IGNORE INTO permission_user_groups(account_id, group_name, instance_id, system_id, position)
SELECT DISTINCT r.account_id, r.role_name,
       CASE WHEN r.instance_scope = '*' THEN NULL ELSE r.instance_scope END, NULL,
       ROW_NUMBER() OVER(PARTITION BY r.account_id ORDER BY r.role_name)
FROM admin_role_assignments r JOIN admins a ON a.account_id = r.account_id AND a.enabled = TRUE
WHERE NOT EXISTS (
    SELECT 1 FROM permission_user_groups g
    WHERE g.account_id = r.account_id AND g.group_name = r.role_name
      AND g.instance_id <=> CASE WHEN r.instance_scope = '*' THEN NULL ELSE r.instance_scope END
      AND g.system_id IS NULL
);
INSERT IGNORE INTO permission_user_groups(account_id, group_name, instance_id, system_id, position)
SELECT a.account_id, 'default', NULL, NULL, 2147483647
FROM accounts a
WHERE NOT EXISTS (SELECT 1 FROM permission_user_groups g WHERE g.account_id=a.account_id AND g.group_name='default' AND g.instance_id IS NULL AND g.system_id IS NULL);

-- Default has no grants. Existing role records are imported as ordered groups and scoped memberships.
