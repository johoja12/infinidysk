-- Publication intents outlive their source item so recovery never blindly replays.
CREATE TABLE par2_attempt_state(slot SMALLINT PRIMARY KEY NOT NULL CHECK(slot=1),revision BIGINT NOT NULL);
INSERT INTO par2_attempt_state(slot,revision) VALUES(1,0);
CREATE TABLE par2_attempts(
 id UUID PRIMARY KEY NOT NULL,
 item_id UUID NOT NULL,
 source BYTEA NOT NULL CHECK(LENGTH(source)=32),
 state TEXT NOT NULL CHECK(state IN ('queued','running','publishing','deferred','healthy','repaired','unsupported','limit','superseded')),
 priority SMALLINT NOT NULL CHECK(priority>=0 AND priority<=255),
 next_attempt BIGINT NOT NULL CHECK(next_attempt>=0),
 updated_at BIGINT NOT NULL CHECK(updated_at>=0),
 revision BIGINT NOT NULL CHECK(revision>=0),
 payload TEXT NOT NULL,
 UNIQUE(item_id,source)
);
CREATE INDEX par2_attempts_due ON par2_attempts(state,priority,next_attempt,id);
