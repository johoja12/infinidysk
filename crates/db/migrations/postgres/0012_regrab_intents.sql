-- Retain outcomes after source removal. Mutations require a successful revision CAS.
CREATE TABLE regrab_intents (
    item_id UUID PRIMARY KEY NOT NULL,
    scope TEXT NOT NULL,
    payload TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 0,
    created_at BIGINT NOT NULL,
    updated_at BIGINT NOT NULL,
    terminal SMALLINT NOT NULL DEFAULT 0
);
CREATE INDEX regrab_intents_updated ON regrab_intents(terminal, updated_at);
