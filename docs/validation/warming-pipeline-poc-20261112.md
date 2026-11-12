# Warming pipeline POC — 2026-11-12

The target is 10 decimal MB/s of newly committed final-media cache data. Cached
replay and aggregate provider bandwidth do not satisfy this target.

## Change

The application now fills a bounded contiguous source window of at most sixteen
cache blocks (67,108,864 bytes). The reader stays open across those blocks, so its
adaptive article window (up to 64 articles with spare provider capacity) can keep working during cache publication. Each block
still has its own strict read, source-revision check, durable publication and
checkpoint. Playback waits and pause/cancel interrupt the read. A settings change
also drops the in-flight window before the next policy is applied. A failed or
interrupted attempt does not publish a partial block.

Source allowance is reserved durably before opening the window. A window is
clipped to the requested contiguous range, remaining daily allowance, and the
next known cached block. Cached blocks remain separate steps and consume no
source allowance. Wire admission remains independent and durable. Interrupted
reservations remain charged conservatively, as before.

The single-block `Queue::tick` shares the same implementation and remains
available to incremental callers. The application uses `Queue::tick_window`.

Warming connection acquisition also watches for pool changes while opening a
new connection. If an existing connection becomes idle, the request can reuse it
while the reserved connection keeps opening. It does not pipeline behind a busy
connection or consume the playback headroom. Foreground and queue connection acquisition keep their existing behavior. Warming
provider selection counts connections still opening when comparing providers on
the same priority level. A cold burst can use several providers without changing
priority or backbone fallback semantics. Delayed-greeting regressions failed before
these changes and pass afterward: one checks reuse of an idle connection, and the
other checks an even split across equal-capacity providers.

Library link queries split item-specific and broad searches into disjoint SQL
branches. Item-specific searches can use the existing item index without changing
root/group/video filters, ordering, pagination or repair authority. No database
migration or persistent job format change is required.

## Deterministic loopback POC

The two arms use seed 1025, 160 articles of 716,800 decoded bytes, a 500 ms reply
delay, the same provider limits, a fresh durable cache and identical policy. The
source contains 114,688,000 bytes. The source-memory budget is 64 MB and provider
capacity is 40 connections. Corpus generation precedes timing. Complete cache
verification follows timing. This is generated data, not live provider evidence.

| Reader lifetime | Seconds | Committed MB/s | BODY commands |
| --- | ---: | ---: | ---: |
| One cache block | 20.450 | 5.608 | 187 |
| Initial eight-block window, 24 articles | 8.135 | 14.098 | 163 |

The current explicit POC uses 96 connections to exercise the new 64-article
ceiling and asserts at least 10 MB/s for the windowed arm. It is ignored
in the ordinary suite because it deliberately injects latency. Run it with:

```sh
cargo test -p infinidysk-prefetch --test queue warming_window_throughput_poc --locked -- --ignored --nocapture
```

A second generated comparison exercises the final 64-article ceiling with 96
provider connections and the same source/latency/cache protocol:

| Reader lifetime | Seconds | Committed MB/s | BODY commands |
| --- | ---: | ---: | ---: |
| One cache block | 20.935 | 5.478 | 187 |
| Sixteen-block window, 64 articles | 5.490 | 20.890 | 161 |

These values are one local comparison, not a guarantee for every provider, file,
archive layout or storage volume.

## Safety coverage

Additional checks exercise cached blocks inside a source range, daily source
allowance, gaps between requested ranges, controls after a committed block, and
source changes between blocks, plus cancellation with more than 32 article
requests in flight. Existing foreground-preemption and stored/encrypted
member checks also exercise the windowed path. The two archive checks now expect
one window to finish their two-block fixture, then verify plaintext and tail bytes.
Library item lookups exercise mixed items, video filtering, groups, roots and
pagination on both database backends when PostgreSQL testing is configured.

## Real-provider POC

An isolated installation has one copied source record under a generic name,
separate SQLite databases and fresh cache folders outside the production cache
namespace. It has no production library mounts or Arr/Plex connections. Routine
health and cleanup are disabled. The provider configuration stays private on the
host. Each arm includes 998 synthetic terminal jobs to exercise ledger overhead.
A bounded 512 MB range is read into a separate folder on the NAS. Provider reads
are real and consume the isolated arm's explicit source/wire allowances.

The baseline completed 536,870,912 newly committed bytes in 159.458 seconds:
**3.367 MB/s**, with 554,282,769 wire bytes. The initial eight-block/24-article
candidate completed the same amount in 82.247 seconds: **6.528 MB/s**. No cached
bytes, playback readers or imports were counted. This did not meet the target.
The sixteen-block/64-article candidate completed in 159.846 seconds: **3.359 MB/s**.
It also missed the target. Per-block timings put about 95% of elapsed time in source
reads. A separate 64 MB connection trace showed requests waiting on new handshakes
while established connections sat idle. The next candidate allows warming
requests to reuse idle connections while those handshakes continue. It retains
the spare-capacity article ceiling of 64 and the sixteen-block source window. The connection floor under pressure, target
quarter reserve, global memory admission, strict reads and playback priority stay
enforced.

An intermediate unoptimized diagnostic with the idle-connection fix committed
536,870,912 uncached bytes in 62.735 seconds: **8.558 MB/s**. This is not a release
image or acceptance result. Disabling warming after its first committed block
stopped further progress, with no source failure attempt recorded. A second
regression then confirmed that six cold warming requests all chose one provider
while its handshakes were pending. Pending connections now contribute to warming
provider selection; queue and playback selection are unchanged.

The final unoptimized diagnostic committed the same range in 76.663 seconds
(**7.003 MB/s**). A sample showed about 108% CPU with no active provider transfers,
so this diagnostic also includes unoptimized processing time. It remains below
target and does not establish release acceptance.

Final POC and rollout results are recorded in PR #268 and the deployment
receipt; neither the first candidate nor its measurements establish acceptance.

## Local validation

- `cargo fmt --all --check`: passed.
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: passed.
- `cargo test --workspace --locked`: passed (the explicit latency POC stays ignored).
- A separate PostgreSQL database passed `postgres_storage_round_trips`, including
  the new mixed-item library lookup cases.

Debug information and incremental compilation were disabled for the full local
suite to bound temporary build storage. No application performance policy changed.

