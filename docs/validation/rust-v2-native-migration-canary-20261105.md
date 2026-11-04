# Native migration canary qualification

A clean build at `47e80a5f1495d7cf1a31fd9ef8fc81350b46a118` ran the real Python
batch pipeline against the actual Rust API, queue, loopback NNTP server, kernel
FUSE mount and cache. The frozen binary SHA256 was
`9bc89a3c51dda408ffd1f9b5f937f7ce4cf6fdba0c0982734a2e55fe7f9cbfc1`.

The fixture used 32 generated articles containing 8 MiB of binary data, a generated
MemoryPack 201 source database, and one sealed mapped package. This was not real
media, original C# package generation, live Plex/Arr or production cutover.

The pipeline admitted the NZB, observed actual queue completion, discovered the
new ID through exact offline source and accepted-job proof, read the expiring
process challenge through the native mount, sampled the source, and created the
separate owned canary links. A finished pass resumed without another submission.
The full generated file matched its expected SHA256. The original symlink's inode,
ctime and target and the source database's byte hash remained unchanged.

Cache evidence is deliberately separate. The initial sampled reads and subsequent
read on the same mount could be served from kernel/memory cache: no persistent
coverage was reported, and an immediate fresh-process providerless read failed.
This short fixture also remains below the conservative playback-backfill threshold.
A complete cold FUSE read in a new process then published verified coverage
`[0, 8388608)`. Another fresh process, with every provider removed before its first
read, returned all 8 MiB with the correct hash. This establishes disk-cache reuse;
a same-process providerless read alone does not.

Three separate genuine 1.x C# writer/import/cache cases passed, including partial
coverage and providerless VFS reuse. The suites run sequentially because they share
an exclusive source-fixture lease. Both privileged kernel FUSE cases passed,
including process-challenge reads. Legacy cache data is copied into separate Rust
storage; the two runtimes never share a writable cache root.

A separate read-through regression was reproduced while checking cache behavior:
seeking to the current position between 64 KiB reads discarded an accumulating
verified block. The regression timed out before the fix. Same-position seeks now
preserve the fill, while real seeks still discard it. The integration test verifies
the completed block through a separate providerless VFS. This is distinct from the
kernel page-cache/sample effect above and does not change backfill thresholds or
introduce speculative fetching.

After that fix: formatting and all-target Clippy passed; 618 workspace tests passed
on SQLite/local PostgreSQL with six fixture-dependent cases ignored. All 27 Python
operations tests passed before the final same-position reader change; the Python
code is unaffected. API generation and UI build passed for the introduced contracts.
All four systemd templates passed syntax verification; none was installed/enabled.

Production image qualification, controlled performance comparison, live integration
acceptance and cutover remain separate gates. Uncertain legacy mutations remain
protected for review rather than being replayed during migration.
