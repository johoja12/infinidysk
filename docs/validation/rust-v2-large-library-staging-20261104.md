# Durable large-library scan validation

Qualified source: `7a1ff9d5`. The filesystem scan now saves private directory/entry/link
checkpoints and publishes the public index only after a whole generation completes.

## Behavior and limits

Additive migration `0022_library_scan_staging` supplies the same contract on SQLite
and PostgreSQL. A new worker nonce fences checkpoint, failure and publication calls
from older work. Configuration/physical-root changes discard the obsolete private
generation. A matching restart resumes accepted progress. Refresh requests arriving
mid-scan remain pending after the older generation publishes.

Filesystem work uses at most 256 entries per checkpoint and yields after about 5,000
new entry/directory operations or a 15-second work slice. Foreground activity suspends
logical work; detached physical I/O retains its permit and cannot publish into the
next worker generation. A 25-second storage deadline retains accepted checkpoints
and the last complete index. Invalid pointers remain unsupported; pointer I/O failure
is no longer silently classified as an absent pointer during catalogue discovery.

The root storage limits are two million entries, two million links, 2 GB of stored
path text and 64 directory levels. These are bounded-work limits, not throughput
promises. Very large flat directories may need repeated enumeration of names already
seen. Public publication uses a database transaction and does not collect every link
in application memory. Derived snapshots/checkpoints for removed roots are deleted;
source items and library files are untouched.

The library UI reports persisted entry/link progress separately from the last complete
results. Existing scan-root settings and setup behavior are unchanged. **Back up
`/config` before upgrading**: the additive migration applies at startup.

## Automated validation

- **581 locked workspace tests passed, zero failed, six default ignores**, including
  actual disposable PostgreSQL 17 and SQLite.
- Formatting, all-target workspace Clippy with warnings denied, API generation and
  the production TypeScript/Vite build passed.
- Both database backends exercise stale-worker rejection, private/public separation,
  foreground suspension, atomic publication, pending refresh preservation and failure
  behavior. SQLite additionally exercises process/database restart and physical-root
  identity reset.
- A generated 6,001-link filesystem test verifies bounded continuation while the old
  visible index stays intact. Existing library conversion, pointer, protected-root,
  read-only classification and Plex matching regressions passed.
- Original C# cache interoperability and kernel-FUSE evidence is recorded separately
  at the preceding cache-recovery checkpoint; this increment changes library scanning
  and additive main-database tables, not the native payload format or provider transport.

## Native restart and Chromium acceptance

Binary SHA-256:
`e8c1c192cae797a0ad65aa2f87052b0d63cc978c2bf7503bdd4dfcfe11f45309`.

An isolated native canary first published a ten-link read-only index. Its generated
folder then grew to **51,001 links**, above the former 50,000-link ceiling. After 5,121
entries were checkpointed, the public group still contained exactly ten links. The
process stopped and resumed with the same scan start identity and 5,121 accepted
entries. Observed public counts were only **10 and 51,001**. Completion cleared the
private progress, acknowledged the requested generation and marked the index current.

Every generated symlink retained its target, inode, mode, modification time and change
time. Providers were disabled; the native media cache retained zero bytes throughout.
The scan performed metadata discovery without warming media. Chromium at 390 px
rendered progress and complete results without page errors or horizontal overflow.

Private artifacts: `/tmp/infinidysk-rust-browser/large-library-*`. Admin authentication
was disabled only in this marked temporary fixture. No production files, live provider,
Plex acceptance, deployment or general performance percentage is claimed.
