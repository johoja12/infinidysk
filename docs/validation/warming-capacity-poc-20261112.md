# Warming capacity follow-up — 2026-11-12

PR #268 removed short-reader churn, idle-connection waits, excessive pending
connection opens and lost durable wire credits. Its optimized isolated tests
committed 536.9 MB at 14.00 MB/s with the native cache and 10.89 MB/s with the
production cache writer. The subsequent production run committed 1,073,741,824
uncached bytes in 112.428 seconds: **9.551 MB/s**, below the 10 decimal MB/s target.
No playback, imports, source failure attempts or wire-budget blocking were counted
in that production run.

This follow-up raises the spare-capacity article ceiling from 64 to 128. The
source window stays at sixteen cache blocks (67,108,864 bytes). A window can
therefore request more of its required articles before the reader reaches them.
The first-priority capacity calculation still targets a quarter reserve. Pools
with less spare capacity use a smaller window, and transfer pressure still reduces
new scheduling to the configured floor of 1–8 articles. Existing replies drain
normally. The bounded connection-waiter rule remains in force.

Per-block strict reads, source-revision checks, durable publication, checkpoints,
source/wire admission, global memory admission and interruption controls do not
change. The UI hint and generated API description show the new ceiling. No schema
or persistent job-format migration is added.

The next isolated POC copies the same healthy source record used in production,
under a generic name, into a separate database. It uses the production cache
writer and a fresh NAS namespace. The tested range starts at 1,073,741,824 and
ends at 2,147,483,648 bytes. That range is still cold in production, so live
acceptance can use the same source and range after rollout. Each isolated source
and wire allowance is 2 GB; the wire allowance is unchanged from the earlier POC.
The source allowance fits the larger 1.07 GB test range. Final measurements and
rollout provenance are recorded in the follow-up PR and the local receipt.
