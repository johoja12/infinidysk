-- Preserve requests arriving during work and keep explicit rechecks ahead of imports.
ALTER TABLE items ADD COLUMN health_requested_at BIGINT;
ALTER TABLE items ADD COLUMN recheck_generation BIGINT NOT NULL DEFAULT 0;
CREATE INDEX health_request_order ON items(recheck_requested, health_requested_at, id);
