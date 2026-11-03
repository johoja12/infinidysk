# C# native cache interoperability fixture

This harness links the **unmodified 1.x** cache writer, filesystem anchoring, repair
ledger, multipart identity and MemoryPack models from a sibling checkout. Only
application telemetry and an unused navigation type are stubbed. It generates generic
synthetic bytes. It does not read production configuration or contact providers.

Use a read-only checkout of fork 1.x `9c104b59`. The default source directory is the
sibling `infinidysk`; override the MSBuild `LegacySourceRoot` property when building
elsewhere. Use .NET 10 and a new absolute destination directory:

```sh
dotnet run --project scripts/fixtures/legacy-native-cache -- /tmp/id-legacy-cache-fixture
export INFINIDYSK_LEGACY_CACHE_FIXTURE=/tmp/id-legacy-cache-fixture/receipt.json
cargo test -p infinidysk-import -p infinidysk-native-cache csharp --locked -- --ignored --test-threads=1
```

The three explicit interoperability tests check:

- Identical source generations from raw compressed blobs, multipart geometry/AES
  parameters, persisted multipart identity and the original per-article repair ledger.
- Actual C# sparse and chunked payloads, including a short last block and partial
  coverage, copied to the Rust format without modifying the original catalogue.
- Full C# file import with atomic source provenance, followed by cache import and
  read/seek verification through both delivery labels in the shared Rust VFS, with
  **no providers configured**. This test is separate from privileged kernel FUSE tests.

Ordinary Rust tests also exercise corrupt bytes, symlinked leaves, missing volume
markers, live owner locks and refusal of mismatched generations/sizes.

The encrypted fixture describes padded ciphertext but stores the final plaintext in
native cache, as 1.x does. These checks qualify cache reuse; they do not qualify a live
media decoder or unknown archive layouts.
