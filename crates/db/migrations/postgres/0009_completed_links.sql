-- Completed-download pointers can be removed without deleting source items.
CREATE TABLE completed_link_removals (
    item_id UUID PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
    removed_at BIGINT NOT NULL
);
