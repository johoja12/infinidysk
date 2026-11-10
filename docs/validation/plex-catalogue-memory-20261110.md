# Large Plex catalogue qualification

Follow-up to #263 / PR #264, tracked in #265.

The first production UI qualification found an existing transport limit: the large TV library reached the 128 MB aggregate XML-tree bound. The native service, consumer mounts, Health controls, named Prefetch history and 32 original-cache canary reads all passed. Warming returned to its prior running state during the follow-up.

Catalogue pagination now converts each page to compact records before retaining it. Unused cast, tags and stream detail trees are released. The shared pagination implementation retains the original identity, duplicate, offset, total-count, completeness, per-response XML, 100,000-row and 128 MB retained-representation checks. A failed Library refresh records a safe reason and retains previous results.

A generated test exceeds the original XML-tree budget while its compact catalogue completes without increasing the bounds. Another test rejects incomplete and duplicate late pages. A read-only diagnostic using the updated client completed all eight production libraries, including the previously failing TV library: 22,790 records and 30,503 explicit file versions. These are metadata results; they do not qualify media playback or download any media payload.

The follow-up changes no cache writer, format, folder, payload or eviction setting. Full source checks, exact-image deployment, and final authenticated production browser/API checks are recorded separately from the diagnostic.

Local validation passed: formatting, strict workspace Clippy, 849 workspace tests (12 ignored), and all 33 Plex transport tests. The UI source is unchanged from the 72-case qualified browser matrix in PR #264. Final-image and live full-Library acceptance remain separate gates.
