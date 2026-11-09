-- A durable snapshot of candidates; source deletion must not reset progress.
CREATE TABLE content_sweep_state(slot SMALLINT PRIMARY KEY NOT NULL CHECK(slot=1),payload TEXT NOT NULL,revision BIGINT NOT NULL DEFAULT 0);
INSERT INTO content_sweep_state(slot,payload,revision) VALUES(1,'null',0);
CREATE TABLE content_sweep_items(item_id BLOB PRIMARY KEY NOT NULL);
