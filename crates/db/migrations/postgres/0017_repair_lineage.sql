-- A local repair ledger must not be silently replaced or rolled back.
CREATE TABLE repair_lineage(slot SMALLINT PRIMARY KEY NOT NULL CHECK(slot=1),ledger_id UUID NOT NULL,epoch BIGINT NOT NULL CHECK(epoch>=0));
