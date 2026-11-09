# Remove the fixed warming delay

The production worker previously called `Queue::tick` from a one-second interval. Each tick can process at most one 4,194,304-byte block. An isolated 60-second production window showed 60 original-cache hits, no foreground readers and no provider traffic, consistent with a roughly 4.2 MB/s scheduler limit while verifying cached data.

The worker now continues after progress and yields to the executor between blocks. It sleeps for one second after idle, blocked or failed work. Source reconciliation and backfill/read admission run once per second between blocks; each step reads current settings and releases the configuration lock before cache/provider I/O. Queue selection, foreground priority, pause/cancel checks, buffer admission, wire/fill allowances, source verification and durable checkpoints still belong to the unchanged single-block `Queue::tick`.

No new concurrent warming jobs, settings, cache formats, storage folders or eviction policy are introduced. Shutdown can interrupt reconciliation, idle sleep or a running step. This change removes a fixed scheduler ceiling; actual throughput can still be limited by storage, provider latency, configured connections, daily budgets or playback priority.

Four deterministic driver tests use Tokio virtual time: 64 generated 2ms steps finish before one virtual second; continuous work still refreshes source policy; idle/errors/reconciliation failure back off; cancellation interrupts all waiting stages. These prove scheduling behavior, not production throughput. Existing real loopback NNTP/cache tests cover cold fills, pause/cancel, daily wire/fill allowances, foreground interruption and restart-safe publication. Production measurements and release acceptance are recorded separately in the PR and issue #261.

Deploy with the existing four original cache folders and eviction disabled. Retain the previous image and verified full application snapshot, take fresh online metadata/configuration backups, drain warming, replace only Rust v2 and restore consumer mounts and the prior pause state. No media payload copying or removal is required.
