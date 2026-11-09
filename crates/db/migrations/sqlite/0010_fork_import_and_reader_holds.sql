-- Import provenance gates reuse of 1.x cache bytes; holds survive process restarts.
CREATE TABLE import_sources (
    item_id BLOB PRIMARY KEY NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    provenance TEXT NOT NULL
);
CREATE TABLE health_reader_holds (
    item_id BLOB PRIMARY KEY NOT NULL REFERENCES items(id) ON DELETE CASCADE
);
