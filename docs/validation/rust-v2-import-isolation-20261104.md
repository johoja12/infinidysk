# Concurrent import connection isolation — 2026-11-04

The earlier two-second queue assertion was not only host timing noise. Replacing its
three-second timed stall with an explicit response gate reproduced an actual stall:
one import remained at Headers, another reached Checking but could not complete.
After ten seconds the provider snapshot showed two requests on one connection in
Reply, no connect failure, no breaker trip and a five-connection configured limit.
Other now-idle connections had already closed. The second job had pipelined its
check behind the first job's held BODY.

A scoped NNTP lane now belongs to each import attempt. Idle/new connections can
serve any lane; a busy connection accepts a scoped request only from its current
lane. An unscoped request marks the connection mixed until idle. Requests within
one lane retain pipelining, provider fallback, existing permits and playback headroom.
Requests waiting for a matching connection wake on the same load-change notification.
Queue BODY, STAT, header/layout probes and its first-health verifier use the lane.

The fixture reports which article is held. Its queue assertion proves the *other*
job reaches completed history before releasing that response; a longer provider
read timeout prevents a timeout from becoming a false concurrency proof. A lower
level test independently holds two BODY responses, queues a STAT in the second
lane and inspects the connection queues before either response is released: one
request on A, two on B. Releasing B completes its BODY and STAT while A remains held.
Both jobs then drain and all eight test permits return; the pool stays at two sockets.

Validation:

- Full NNTP and queue package tests pass, including the held-response regression.
- The entire 23-test queue import suite passes five additional runs under normal
  concurrent test execution (115 additional passing cases).
- Formatting and all-target workspace Clippy pass.
- Evidence: `/tmp/rust-fork-queue-stall-rca.log`,
  `/tmp/rust-fork-queue-lanes-tests.log`, and
  `/tmp/rust-fork-queue-lanes-repeat.json`.
- Complete workspace qualification follows the feature increments; no earlier
  stopped run is being reclassified as green. No production service was changed.

This is correctness evidence for independent imports and retained intra-job
pipelining. It is not a throughput benchmark or a guarantee of progress when every
usable connection itself is stalled.
