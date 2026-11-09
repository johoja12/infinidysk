-- Acceptance receipts survive queue/history pruning. Never infer safe replay from
-- missing history after an uncertain client response.
CREATE TABLE migration_submissions(
 intent TEXT PRIMARY KEY NOT NULL,
 scope TEXT NOT NULL,
 payload_sha256 TEXT NOT NULL,
 job_id BLOB NOT NULL,
 created_at BIGINT NOT NULL
);
