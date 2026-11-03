-- Keep large read-only classification snapshots separate from configuration loading.
CREATE TABLE plex_classification_snapshot (
 slot SMALLINT PRIMARY KEY NOT NULL CHECK(slot=1),
 payload TEXT NOT NULL
);
