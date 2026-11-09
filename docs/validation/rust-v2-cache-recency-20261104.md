# Verified cache reads survive recency writer contention

A deterministic regression reproduced this defect: even after reading and verifying
a cached block, playback waited for a synchronous `entries.used` update. An unrelated
SQLite writer blocked that bookkeeping; its failure discarded valid bytes and made
the caller fall back to the source.

Verified payloads now return independently of that update. A queue holds at most
1,024 pending events and coalesces up to 128 events per write transaction. The worker
owns no cache object, payload-folder lock or reader lease. Recency is best-effort:
saturated/failed updates are counted and may leave approximate eviction timestamps.
Updates take the maximum timestamp so an older queued touch cannot overwrite a newer
fill. Content validation, ownership, reader protection and production I/O deadlines
remain unchanged. Diagnostics expose read timeouts and pending/dropped recency updates.

The writer-lock regression failed on the old code and passed with the fix. It checks
valid bytes while a separate connection holds a write transaction, then checks that
recency persists after releasing the lock. Its generous test-only I/O budget separates
correctness from host timing. No latency percentage is claimed.

The subsequent full workspace run passed 604 tests on SQLite and PostgreSQL 17, with
zero failures and six default ignores. The existing warmed-playback zero-NNTP case
also passed. An earlier parallel warmed-playback case observed one source request;
the new lock test proves the defect above, but does not establish the cause of that
particular earlier observation. Read-timeout diagnostics were added to distinguish
future cases. No configuration or setup-wizard change is introduced.

Five complete main-library repeats (540 additional test executions at 16 threads)
also passed. API generation and the production TypeScript/Vite build passed.
