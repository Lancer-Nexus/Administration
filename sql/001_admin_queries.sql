-- Apply explicitly to the Administration database. No administrator is created automatically.
CREATE TABLE IF NOT EXISTS admins (
    account_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
    enabled BOOLEAN NOT NULL DEFAULT FALSE
);
CREATE TABLE IF NOT EXISTS admin_role_permissions (
    role_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    permission VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (role_name, permission)
);
CREATE TABLE IF NOT EXISTS admin_role_assignments (
    account_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    role_name VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    instance_scope VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (account_id, role_name, instance_scope),
    FOREIGN KEY (account_id) REFERENCES admins(account_id)
);
CREATE TABLE IF NOT EXISTS admin_query_keys (
    account_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    idempotency_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    PRIMARY KEY (account_id, idempotency_key)
);
CREATE TABLE IF NOT EXISTS admin_audit (
    audit_id BIGINT AUTO_INCREMENT PRIMARY KEY,
    created_at_utc DATETIME(6) NOT NULL,
    account_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    correlation_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    source VARCHAR(128) NOT NULL,
    command_kind VARCHAR(32) NOT NULL,
    target_instance_id VARCHAR(96) NULL,
    result VARCHAR(32) NOT NULL
);
INSERT IGNORE INTO admin_role_permissions (role_name, permission) VALUES
    ('cluster-observer', 'admin.help'), ('cluster-observer', 'status.read'),
    ('cluster-observer', 'instance.read');
