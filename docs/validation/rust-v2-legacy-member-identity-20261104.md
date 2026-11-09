# Exact legacy-member identity

The Rust mapper reads the original NZB's file/segment order, optional ordinals and
wire-byte counts. SAB-normalized queue metadata is not equivalent. Direct and stable
archive identities use the exact 1.x domains, integer byte order, string framing and
range rules. Mapping refuses ambiguous articles, unresolved parts, invalid ranges and
unsupported encodings/structures; it never guesses a member from its title or size.

The original C# implementation was compiled in a temporary project that linked its two
pure identity source files read-only. Its direct, release and stable-archive vectors
match Rust. The vectors include wrapped message IDs, an absent ordinal and XML order
that differs from ordinal order. No C# runtime is required by the Rust product.

Explicit offline capture reconverts a copied legacy member with no provider configured.
It must match the current imported source's canonical fingerprint, size and original
generation. Derived evidence is saved only while the exact current metadata bytes,
blob ID, size, provenance and visibility still match. Original metadata, journals and
media are not rewritten. Existing imports and native cache identities remain readable;
the new evidence is optional, and normal import adds no repeated NZB parsing overhead.

`POST /api/v1/migration/legacy-proof` compares a sealed package/member/payload identity
with that preserved import. One owned mapping task may run at a time. HTTP cancellation
or a soft timeout does not release the slot while a blocking source parse still runs.
The response fences current metadata and repair revision while holding a zero-byte
read handle. Its ID path refers to the preserved original item UUID.

**This is an identity proof, not a playback certificate.** The response explicitly
sets `payload_validated: false`. Physical canary reads, mount identity, guarded link
planning and batch acknowledgement remain separate acceptance steps. Inconclusive
old geometry remains unknown rather than causing provider traffic during capture.

## Validation

609 locked workspace tests passed, zero failed, six default ignores, with actual
SQLite and PostgreSQL 17. Formatting and warnings-denied all-target Clippy passed.

- Independent original C# direct/release/archive vectors match Rust.
- Negative tests reject duplicate/missing articles, incomplete XML, negative wire
  bytes and overflowing/invalid member ranges.
- A generated MemoryPack multipart import captures an exact identity, reuses it after
  the offline source is absent and refuses later current-metadata changes.
- SQLite/PostgreSQL tests cover wrong metadata bytes, sizes and provenance, successful
  compare-and-swap, hidden-source refusal and deletion cleanup.
- Actual HTTP tests use **explicitly seeded already-derived evidence**. They prove
  package/member comparison, current ID/revision reporting, mismatch refusal and
  retention of old evidence after source changes. This HTTP fixture is distinct from
  the C# vector and offline conversion tests; it claims no live provider acceptance.
- A separate gated physical-cache test proves deadlines retain queued I/O protection
  after the caller returns. Functional prediction/cache fixtures use a longer test-only
  storage budget; production deadlines are unchanged.

Private evidence: `/tmp/rust-fork-article-identity-csharp/`,
`/tmp/rust-fork-source-mapping-*` and `/tmp/rust-fork-cache-deadline-focused.log`.
No production source, library link, Arr state, image or deployment is changed. No user
configuration option or setup-wizard version change is introduced.

API generation and the production TypeScript/Vite build passed. Three original C#
cache-writer/import interoperability cases and two privileged kernel-FUSE cases
passed separately on the current product code. The optional external NZB corpus
remained unavailable. Native cache reuse is verified copying into separate Rust
storage; this does not authorize sharing writable 1.x cache/config directories.
