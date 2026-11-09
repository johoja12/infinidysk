-- A conclusive check applies only to one exact source and repair revision.
CREATE TABLE repair_observations(item_id UUID PRIMARY KEY NOT NULL REFERENCES items(id) ON DELETE CASCADE,revision BYTEA NOT NULL CHECK(LENGTH(revision)=32));
