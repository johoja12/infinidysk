-- Reservation is atomic: lost acknowledgements can only overcharge, never replay credit.
CREATE TABLE prefetch_wire_budget(slot SMALLINT PRIMARY KEY NOT NULL CHECK(slot=1),day BIGINT NOT NULL,charged BIGINT NOT NULL CHECK(charged>=0),granted BIGINT NOT NULL CHECK(granted>=0));
INSERT INTO prefetch_wire_budget(slot,day,charged,granted) VALUES(1,0,0,0);
