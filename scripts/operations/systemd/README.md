# Rust migration submission service

These templates run bounded passes of `rust-import-runner.py` against one sealed
package. Each pass observes durable acceptance before dispatching. A timeout or
missing response never permits blind replay. This is an acceptance service;
completion does not acknowledge a batch or promote library links.

Install the reviewed operations scripts under `/opt/infinidysk-tools`, create the
unprivileged `infinidysk-migration` account, and grant it read-only traversal of the
sealed package directory. Install the service and timer in `/etc/systemd/system`.
Copy `rust-import.env.example` to
`/etc/infinidysk/rust-migration/canary.env`, replacing every placeholder with the
retained package digest, exact tested Rust commit, explicit API origin and package
path. Keep that plan unchanged while resuming its journal. Root owns the environment
file; no API key belongs there.

Place the API key in root-owned mode-0600
`/etc/infinidysk/rust-migration/canary.api-key`. Systemd supplies a separate private
credential. The service can write only its private state directory under
`/var/lib/infinidysk-rust-import-canary`; the source package stays read-only.
The example uses `/srv` because home directories are inaccessible to this service.

Review the exact unit and plan, run `systemd-analyze verify`, then start one service
pass for inspection before enabling the instance timer. Enabling is an operator
action; these files install or start nothing by themselves. Stop the timer before
changing packages or source commits. Use a new instance and journal for a different
plan. Preserve old journals, including incomplete ones, for explicit recovery.

The timer observes every 30 seconds after the preceding pass finishes. Imports are
bounded to two release slots. Failed, completed, missing-history and uncertain
submissions remain durable outcomes rather than automatic retries. Member/source
validation and canary link planning run separately with
`rust-migration-canary.py`; neither service nor script acknowledges production
cutover. The separate batch service below adds frozen priority and canary disposition orchestration.

Validation: `systemd-analyze verify --man=no` accepted both templates. The service
was not installed, enabled or run against production.

For the two-slot canary workflow use `infinidysk-rust-batches@` with a separate
`%i-batches.env` copied from `rust-batches.env.example`. Its state directory and
journal are separate from the single-package service. Install all reviewed Python
operations modules together. The backend must see the same explicit offline
configuration path, mounted read-only when it runs in a container. The canary
mount and original library are also read-only inputs to these scripts.

The batch service allows 600 seconds per discovery/validation phase and 1800
seconds for a pass. Large batches may need a reviewed unit override; when changing
`--phase-timeout`, leave time for both phases and the last bounded import request.
A timeout preserves child journals and owned canary plans. It does not acknowledge
a batch or authorize replay. Never run both services against the same acceptance
journal. Stop the timer for explicit dispositions or manual rollback.
