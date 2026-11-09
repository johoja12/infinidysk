CREATE TABLE prefetch_history(id BLOB PRIMARY KEY NOT NULL,recorded_at BIGINT NOT NULL,payload TEXT NOT NULL);
CREATE INDEX prefetch_history_recent ON prefetch_history(recorded_at,id);
