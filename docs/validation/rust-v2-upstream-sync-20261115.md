# Rust v2 upstream sync — 2026-11-15

## Source and preservation

Started on clean `main` at `1f9ee22e10e9d66fb0545984b0efcdc183bbe04b`,
matching origin/main. Origin is `johoja12/infinidysk`; upstream is
`infinidysk/infinidysk`. The last sync was committed on 14 November at
13:21:53 Dubai time and included upstream through `53ba6cc5a2b85afa898ae3902f04ce89186c8d22`.

The dated `git stash push --include-untracked` attempt reported no changes.
No new stash was created. Existing stashes remain intact. Both remotes were
fetched before comparing refs. Merge and validation use an isolated worktree.

One upstream commit is incoming:
`b07e7a79e0de802de92a4e93a776196fb58be4ad` — search again when an accepted
replacement fails while hidden. The hourly reconciliation checks for failed
grabs, waits for queued replacements or active searches, and continues the
same attempt without another blocklist or repair-attempt charge.

## Fork feature mapping and conflict decisions

The capability specification remains [fork migration](../fork-v2-migration.md)
and the [fork parity audit](../rust-v2-fork-parity-audit-20261107.md).
Against the common upstream ancestor there are 351 fork-specific paths and six
incoming paths. All six overlap fork work. Outside those paths, 343 fork paths
are unchanged; the other two extend the existing durable Arr repair journal.
No fork capability is superseded.

| Capability | Result |
| --- | --- |
| R01–R02 repair identity and original-download provenance | Preserved. A retry retains the original owner, file identity and download provenance. |
| R03–R05 reader safety, urgent repair and fair sweep | Preserved. Readers and remap operations defer retries. Existing authority, source-scope and CAS checks still fence mutations. Sweep and urgency code are unchanged. |
| R06 guarded regrabs | Extended. A failed accepted replacement continues the existing journal from its acknowledged blocklist into checkpointed deletion and search. Pending outcomes are observed before further mutation. The saved owner permits resumption after the Arr file disappears. |
| R07 PAR2 and publication | Unchanged. |
| C01–C05 shared cache, compatibility and administration | Unchanged. |
| P01–P05 Plex and Smart Prefetch | Unchanged, including strict warming lanes, wire grants, spare-capacity admission and retained-memory lookup. |
| L01–L04 library and playback | Unchanged, including the latest Plex classification deadline fix and startup traces. |
| M01–M04 migration and operations | Unchanged. No schema, blob format or pointer migration. |
| S01–S03 durable removal, cancellation, multipart and archive readiness | Unchanged. |
| O01 provenance and observability | Preserved. Known Arr retry failures use a warning with a reason and no backtrace. |
| Independent import and health sampling | Unchanged. |

Text conflicts were in Cargo.lock, Arr exports and Arr replacement logic.
The lockfile retains both md-5 and the incoming jiff test dependency. Arr exports
retain the tracked journal APIs and add the upstream failure-observation APIs.
The file-only owner search guard is retained before the shared delete/search
helper. No whole-file ours/theirs resolution was used.

The automatically merged upstream health retry required a semantic adaptation:
its direct delete/search call would bypass the fork's journal. Health instead
uses the existing coordinator and its versioned CAS checkpoints. No new journal
fields or stages are introduced. Acknowledged blocklisting is retained, and the
highest observed grab identifies the failed search baseline. Existing root-folder,
source, configured Arr and reader checks still precede mutation. Search-rate
reservations apply to a new fallback, not resumption of an uncertain mutation.

Regression fixtures cover successful fallback, queued replacement deferral,
lost delete acknowledgement after the file disappears, and lost search
acknowledgement observed by command identity. Each retains one repair attempt
and one blocklist. Lost acknowledgements do not duplicate deletion or search;
the delete case also exercises the independent read-only journal observer.

## Validation

Rust 1.99.1 and Node 26.13.1 are used without changing version pins.
UI dependencies were installed with optional packages and an isolated npm cache.
A disposable PostgreSQL 17 fixture enables PostgreSQL cases. Debug symbols and
incremental artifacts are disabled for local dev/test builds to bound disk use;
debug assertions remain enabled. Cargo processes do not overlap.

Passed local gates:

- Formatting and strict workspace/all-target Clippy.
- Workspace tests: 982 passed, zero failed, 13 ignored, PostgreSQL enabled.
- Four focused hidden-replacement retry and acknowledgement-loss regressions.
- Cargo deny: advisories, bans, licenses and sources under repository policy.
- UI TypeScript/Vite build and 114 browser fixtures across three widths and both themes.
- Migration operations: 27 tests; four systemd templates and mocked announcement checks.

`cargo build --release --locked` passed in 896.1 seconds on local
Ubuntu 24.04 Linux x64, with the final embedded UI and the repository fat-LTO,
single-codegen-unit profile. The release binary also passed an OpenAPI startup
smoke check. No source pins or release-profile settings were relaxed.

Logs are retained under `scrap/upstream-sync-20261115/` in the main checkout.
These are local fixture and build results. They do not establish macOS, native
Ubuntu 26.04 x64/arm64 packaging, remote CI, live Usenet/Plex/Arr/NAS behavior,
or production acceptance. No deployment is part of this sync.
