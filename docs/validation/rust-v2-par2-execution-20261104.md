# PAR2 reconstruction and provider execution checkpoint

Source under test: `69cdc090`, following `fa1677d5`, `031248ca` and `7082ab26`.
This document changes no Rust source. The 1.x behavior specification remains
`9c104b5961abe67c14e6565932e48ea340f1dbd3` and was not modified.

## Implemented

- Bounded GF16 reconstruction, independent-equation selection, missing-slice limits
  and cancellation, verified against independent par2cmdline recovery packets.
- Incremental packet framing, damaged/truncated-header resynchronization and
  per-volume scan/hash-work/metadata ceilings. Recovery rows stop once sufficient.
- Ordered damage inspection with IFSC and whole-file hashes for intact files.
- A publication-proof type returned only after final whole-file verification.
- A provider-facing health execution API requiring complete trusted source ranges
  and exact imported PAR2 descriptors. It uses an isolated NNTP lane, validates
  returned geometry, reuses one current article and moves CPU work off Tokio workers.
- Separate outcomes for intact data, verified reconstructed slices, excessive damage,
  provider uncertainty, source changes, cancellation and resource exhaustion.
- Large-slice memory preflight reserves a minimum workable attempt. The actual
  missing count is checked after census rather than reserving the policy maximum.

Defaults retain eight missing slices and a 256 MB-scale RAM allowance, with a
16 GB-scale release, 512 MB-scale recovery-volume scan and 100,000 BODY admissions.
Internal ceilings use powers of two; decoded-read allowance is not wire accounting.
No new persisted setting or setup-wizard control is introduced by these APIs.

## Validation

- `cargo fmt --all --check`: passed.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: passed.
- `cargo test --workspace --locked`: **479 passed, 0 failed, 6 ignored**.
  This used the task-owned loopback PostgreSQL 17 fixture as well as SQLite.
- Pure PAR2: **14 passed**, including recovery-byte equality, full-file hashes,
  metadata conflicts, damage, forged recovery contents, limits, cancellation,
  chunk boundaries, early completion and full 32-bit exponents.
- Provider executor: **6 passed**, including a missing article crossing two slices,
  exact restored bytes, intact-data checks, early budget cutoffs, untrusted bindings,
  provider outage, cancellation and permit return.
- Original unmodified 1.x C# cache writer interoperability: **3 passed** separately.
  Sparse/chunked/partial cache layouts, multipart and repair-ledger generations,
  full import and provider-free VFS reads remain compatible.
- Actual privileged kernel FUSE: **2 passed** separately, including native cache.

The optional local NZB corpus was not supplied. Ordinary workspace runs deliberately
ignore privileged FUSE and original-writer fixture tests; the separate results above
qualify those five ignored cases. They do not qualify live provider or media playback.

## Still required

This checkpoint does **not** connect the execution API to automatic repair. R07
still needs durable source discovery, worker scheduling/outcomes and cooldowns;
C03 still needs article-patch publication and cache revision integration. Partial
or untrusted archive ranges cannot currently enter this execution API. Library,
Smart Prefetch, diagnostics/recovery and migration gaps remain in the inventory.

No deployment, production-data mutation, live provider test or migration cutover
was performed. Draft PR #235 remains a development branch.
