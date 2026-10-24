# Removing completed file links after import

A WebDAV `DELETE` of an individual file under `/completed-symlinks` removes that
virtual completed entry persistently. Its `/content` file, stable `/.ids` target,
other pack members and download history are preserved. This also works when
WebDAV read-only enforcement is enabled because it removes import metadata only.
Clients should verify their independent library link before deleting a completed
entry. Removing a whole release folder keeps its existing history-cleanup behavior.

Removal state is stored by file identity under
`{CONFIG_PATH}/completed-symlink-deletions/`, so it survives restarts and belongs in
configuration backups. A new file with the same name and a different identity
remains visible. Existing completed entries are unchanged until explicitly deleted;
there is no database migration or automatic cleanup of older imports.
