-- Immutable source evidence, retained even when the imported item disappears.
CREATE TABLE legacy_repair_holds(
 evidence_id TEXT PRIMARY KEY NOT NULL,
 item_id UUID,
 kind TEXT NOT NULL,
 payload TEXT NOT NULL,
 request_id TEXT,
 observed_at BIGINT NOT NULL DEFAULT 0,
 resolved_at BIGINT,
 proof TEXT,
 created_at BIGINT NOT NULL
);
CREATE INDEX legacy_repair_holds_item ON legacy_repair_holds(item_id);
CREATE INDEX legacy_repair_holds_request ON legacy_repair_holds(request_id);
