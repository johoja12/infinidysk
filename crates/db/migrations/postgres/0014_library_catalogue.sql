-- Read-only catalogue snapshots are replaced atomically after a complete scan.
CREATE TABLE library_catalogue (
 root TEXT PRIMARY KEY NOT NULL,
 scope TEXT NOT NULL,
 scanned_at BIGINT,
 requested_at BIGINT NOT NULL DEFAULT 0,
 requested_generation BIGINT NOT NULL DEFAULT 0,
 completed_generation BIGINT NOT NULL DEFAULT 0,
 attempted_generation BIGINT NOT NULL DEFAULT 0,
 attempted_at BIGINT NOT NULL DEFAULT 0,
 error TEXT
);
CREATE TABLE library_links (
 root TEXT NOT NULL REFERENCES library_catalogue(root) ON DELETE CASCADE,
 path TEXT NOT NULL,
 relative_path TEXT NOT NULL,
 group_name TEXT NOT NULL,
 item_id UUID NOT NULL,
 pointer_hash TEXT NOT NULL,
 link_kind SMALLINT NOT NULL,
 video SMALLINT NOT NULL,
 PRIMARY KEY(root,path)
);
CREATE INDEX library_links_item ON library_links(item_id,root,path);
CREATE INDEX library_links_group ON library_links(root,group_name,path);
