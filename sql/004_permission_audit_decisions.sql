-- Make the durable mutation audit explicit about the authorization decision and outcome.
ALTER TABLE permission_audit
    ADD COLUMN decision VARCHAR(16) NOT NULL DEFAULT 'allowed' AFTER revision,
    ADD COLUMN reason VARCHAR(128) NOT NULL DEFAULT 'legacy_record' AFTER decision,
    ADD COLUMN result VARCHAR(32) NOT NULL DEFAULT 'recorded' AFTER reason;

ALTER TABLE permission_audit
    ADD CONSTRAINT chk_permission_audit_decision CHECK (decision IN ('allowed', 'denied'));
