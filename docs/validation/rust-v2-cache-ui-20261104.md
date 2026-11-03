# Rust cache inspection UI checkpoint — 2026-11-04

This UI-only increment follows `b7f559978722c825d47dadfa1898dfbc6069511c`.
The existing bounded authenticated cache APIs and backend behavior do not change.
No schema, configuration or setup-wizard change is introduced.

TypeScript and Vite production build pass. Chromium uses the actual local API/UI and
an 8,388,608-byte generated NNTP source with a matching full-file SHA-256. No responses
or cache records are simulated. The checks cover:

- Manual storage diagnostics report verified folder identity/free space.
- Empty eviction history has disabled pagination and an explicit empty state.
- Stored-block verification checks both blocks and finishes the pass.
- Flipping one byte in the disposable payload causes exactly one block to lose
  coverage. A normal verified read restores full coverage and the expected hash.
- Renaming the disposable cache directory reports unavailable/uncertain identity.
  A probe retains all coverage and returns a retry cursor. Restoring the same
  directory and retrying verifies both blocks successfully.
- Confirmed manual eviction clears coverage and appears in persistent history after
  a page reload. The 390px layout fits without horizontal overflow. No page errors.

Local browser receipt: `/tmp/infinidysk-rust-browser/cache-tools-validation.json`.
Canary binary SHA-256:
`cfa7214eb4243e611319fb2441b9a00836c7e4d2bafe294cba296145f2fc935f`.
RustEmbed debug mode reads `ui/dist`; the UI artifact has a separate sorted path/hash
manifest at `/tmp/infinidysk-rust-browser/cache-tools-ui-manifest.json`, with its digest
in the browser receipt. This is not release-image or production acceptance evidence.

Backend qualification remains the classification checkpoint's 410 passes, one existing
queue deadline failure and six ignored tests. The queue case passes in isolation.
Two privileged kernel and three original C# writer checks passed separately. The
workspace suite is not repeated for this UI-only change. Detailed traffic attribution,
offline catalogue reconstruction and PAR2 repair-revision execution remain open.

All fault injection stays inside the generated private canary root. No production
cache, real account, live Usenet provider, playback decoder or performance test is used.
