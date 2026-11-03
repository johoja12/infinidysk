-- Playback/warming urgency survives inconclusive provider checks and restarts.
CREATE TABLE urgent_health (
    item_id UUID PRIMARY KEY NOT NULL REFERENCES items(id) ON DELETE CASCADE
);
