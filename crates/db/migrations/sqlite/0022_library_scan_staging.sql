-- Durable private scan generations. Public links change only on complete publication.
CREATE TABLE library_scan (
 root TEXT PRIMARY KEY NOT NULL REFERENCES library_catalogue(root) ON DELETE CASCADE,
 scope TEXT NOT NULL, worker TEXT NOT NULL, device TEXT NOT NULL, inode TEXT NOT NULL,
 generation BIGINT NOT NULL, visited BIGINT NOT NULL DEFAULT 0, linked BIGINT NOT NULL DEFAULT 0,
 path_bytes BIGINT NOT NULL DEFAULT 0, started_at BIGINT NOT NULL, updated_at BIGINT NOT NULL
);
CREATE TABLE library_scan_dirs (
 root TEXT NOT NULL REFERENCES library_scan(root) ON DELETE CASCADE,
 path TEXT NOT NULL, depth BIGINT NOT NULL, done SMALLINT NOT NULL DEFAULT 0,
 PRIMARY KEY(root,path)
);
CREATE INDEX library_scan_pending ON library_scan_dirs(root,done,depth,path);
CREATE TABLE library_scan_seen (
 root TEXT NOT NULL REFERENCES library_scan(root) ON DELETE CASCADE,
 path TEXT NOT NULL, PRIMARY KEY(root,path)
);
CREATE TABLE library_scan_links (
 root TEXT NOT NULL REFERENCES library_scan(root) ON DELETE CASCADE,
 path TEXT NOT NULL, relative_path TEXT NOT NULL, group_name TEXT NOT NULL,
 item_id BLOB NOT NULL, pointer_hash TEXT NOT NULL, link_kind SMALLINT NOT NULL, video SMALLINT NOT NULL,
 PRIMARY KEY(root,path)
);
