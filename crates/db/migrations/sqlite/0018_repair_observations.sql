-- A conclusive check applies only to one exact source and repair revision.
CREATE TABLE repair_observations(item_id BLOB PRIMARY KEY NOT NULL REFERENCES items(id) ON DELETE CASCADE,revision BLOB NOT NULL CHECK(LENGTH(revision)=32));
