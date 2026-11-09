//! Drive bounded warming steps without a fixed delay between successful blocks.
use std::time::Duration;
use tokio::time::Instant;
use tokio_util::sync::CancellationToken;

const IDLE: Duration = Duration::from_secs(1);

/// `step` still owns playback priority, current settings, admission and durable budgets.
/// Reconcile sources periodically; back off on idle, blocked work or errors, and
/// yield after progress so a hot cache cannot monopolize the executor.
pub(super) async fn run<R, S>(
    stop: &CancellationToken,
    mut reconcile: impl FnMut() -> R,
    mut step: impl FnMut() -> S,
) where
    R: std::future::Future<Output = bool>,
    S: std::future::Future<Output = anyhow::Result<bool>>,
{
    let mut refresh_at = Instant::now();
    let mut ready = true;
    loop {
        if stop.is_cancelled() {
            break;
        }
        if Instant::now() >= refresh_at {
            ready = tokio::select! {
                biased;
                () = stop.cancelled() => break,
                ready = reconcile() => ready,
            };
            refresh_at = Instant::now() + IDLE;
        }
        let progress = if ready {
            tokio::select! {
                biased;
                () = stop.cancelled() => break,
                result = step() => match result {
                    Ok(progress) => progress,
                    Err(error) => {
                        tracing::warn!(reason=%error, "warming step deferred");
                        false
                    }
                },
            }
        } else {
            false
        };
        tokio::select! {
            biased;
            () = stop.cancelled() => break,
            () = async {
                if progress {
                    tokio::task::yield_now().await;
                } else {
                    tokio::time::sleep(IDLE).await;
                }
            } => {},
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::Cell;

    #[tokio::test(start_paused = true)]
    async fn successful_blocks_continue_at_io_speed_without_waiting_for_the_timer() {
        let stop = CancellationToken::new();
        let blocks = Cell::new(0);
        let refreshes = Cell::new(0);
        let start = Instant::now();
        run(
            &stop,
            || async {
                refreshes.set(refreshes.get() + 1);
                true
            },
            || async {
                tokio::time::sleep(Duration::from_millis(2)).await;
                blocks.set(blocks.get() + 1);
                if blocks.get() == 64 {
                    stop.cancel();
                }
                Ok(true)
            },
        )
        .await;
        assert_eq!(blocks.get(), 64);
        assert_eq!(refreshes.get(), 1);
        assert!(start.elapsed() < IDLE, "64 blocks must not take 64 seconds");
    }

    #[tokio::test(start_paused = true)]
    async fn continuous_work_still_reconciles_sources_and_observes_updated_policy() {
        let stop = CancellationToken::new();
        let enabled = Cell::new(true);
        let refreshes = Cell::new(0);
        let attempts = Cell::new(0);
        let blocks = Cell::new(0);
        let times = std::cell::RefCell::new(Vec::new());
        run(
            &stop,
            || async {
                let n = refreshes.get() + 1;
                refreshes.set(n);
                times.borrow_mut().push(Instant::now());
                enabled.set(n != 2);
                if n == 3 {
                    stop.cancel();
                }
                true
            },
            || async {
                attempts.set(attempts.get() + 1);
                if !enabled.get() {
                    return Ok(false);
                }
                tokio::time::sleep(Duration::from_millis(40)).await;
                blocks.set(blocks.get() + 1);
                Ok(true)
            },
        )
        .await;
        assert!(
            blocks.get() >= 20,
            "active work must not wait between blocks"
        );
        assert_eq!(
            attempts.get(),
            blocks.get() + 1,
            "disabled policy backs off"
        );
        let times = times.borrow();
        assert_eq!(times.len(), 3);
        assert!(times.windows(2).all(|w| {
            let gap = w[1] - w[0];
            gap >= IDLE && gap <= IDLE + Duration::from_millis(50)
        }));
    }

    #[tokio::test(start_paused = true)]
    async fn idle_errors_and_failed_reconciliation_back_off_without_spinning() {
        for mode in 0..3 {
            let stop = CancellationToken::new();
            let refreshes = Cell::new(0);
            let attempts = Cell::new(0);
            let start = Instant::now();
            run(
                &stop,
                || async {
                    refreshes.set(refreshes.get() + 1);
                    if refreshes.get() == 4 {
                        stop.cancel();
                    }
                    mode != 2
                },
                || async {
                    attempts.set(attempts.get() + 1);
                    if mode == 1 {
                        anyhow::bail!("generated step failure");
                    }
                    Ok(false)
                },
            )
            .await;
            assert_eq!(start.elapsed(), IDLE * 3);
            assert_eq!(attempts.get(), if mode == 2 { 0 } else { 3 });
        }
    }

    #[tokio::test(start_paused = true)]
    async fn cancellation_interrupts_idle_inflight_steps_and_reconciliation() {
        for mode in 0..3 {
            let stop = CancellationToken::new();
            let cancel = stop.clone();
            tokio::spawn(async move {
                tokio::time::sleep(Duration::from_millis(10)).await;
                cancel.cancel();
            });
            let start = Instant::now();
            run(
                &stop,
                || async {
                    if mode == 2 {
                        std::future::pending::<()>().await;
                    }
                    true
                },
                || async {
                    if mode == 1 {
                        std::future::pending::<()>().await;
                    }
                    Ok(false)
                },
            )
            .await;
            assert_eq!(start.elapsed(), Duration::from_millis(10));
        }
    }
}
