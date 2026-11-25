//! Final-media read-through is shared by FUSE, `WebDAV` and STRM. Cold bytes are
//! returned as they arrive; only complete blocks without any filler are published.
use std::future::Future;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::{
    Arc,
    atomic::{AtomicU64, Ordering},
};
use std::task::{Context, Poll, ready};

use bytes::Bytes;
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, Key, Lease};
use infinidysk_stream::Gaps;
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};
use tokio::sync::OwnedSemaphorePermit;
use tokio::time::Instant;

use crate::Reader;

type Lookup = Pin<Box<dyn Future<Output = Option<Bytes>> + Send>>;
type Admission = Pin<Box<dyn Future<Output = Option<OwnedSemaphorePermit>> + Send>>;
struct Fill {
    start: u64,
    generation: u64,
    bytes: Vec<u8>,
    permit: OwnedSemaphorePermit,
}
pub struct NativeReader {
    inner: Box<Reader>,
    native_cache_bytes: Arc<AtomicU64>,
    cache: Arc<Cache>,
    lease: Arc<Lease>,
    gaps: Arc<Gaps>,
    pos: u64,
    end: u64,
    source_pos: u64,
    source_seeking: bool,
    lookup: Option<Lookup>,
    checked: Option<u64>,
    hit: Option<Bytes>,
    fill: Option<Fill>,
    admission: Option<Admission>,
    attempted_fill: Option<u64>,
    retry_fill_at: Option<Instant>,
    backfill: crate::backfill::Tracker,
}
impl NativeReader {
    pub(crate) fn wrap(
        inner: Reader,
        cache: Arc<Cache>,
        key: Key,
        gaps: Arc<Gaps>,
        native_cache_bytes: Arc<AtomicU64>,
        backfills: Arc<crate::Backfills>,
    ) -> Reader {
        let backfill = crate::backfill::Tracker::new(backfills, key.clone());
        let Some(lease) = cache.lease(key) else {
            return inner;
        };
        let end = lease.key().size();
        Reader::Native(Box::new(Self {
            inner: Box::new(inner),
            native_cache_bytes,
            cache,
            lease,
            gaps,
            pos: 0,
            end,
            source_pos: 0,
            source_seeking: false,
            lookup: None,
            checked: None,
            hit: None,
            fill: None,
            admission: None,
            attempted_fill: None,
            retry_fill_at: None,
            backfill,
        }))
    }
    pub(crate) fn set_end(&mut self, end: u64) {
        self.end = end.min(self.lease.key().size());
        self.inner.set_end(end);
    }
    fn prepare_fill(&mut self, cx: &mut Context<'_>, block: u64, within: usize) -> Poll<()> {
        if self.attempted_fill == Some(block) {
            return Poll::Ready(());
        }
        let buffer = if within != 0 {
            self.cache.resume_playback_prefix(&self.lease, self.pos)
        } else if self.cache.can_fill(self.lease.key())
            && self.retry_fill_at.is_none_or(|at| Instant::now() >= at)
        {
            let admission = self.admission.get_or_insert_with(|| {
                let cache = self.cache.clone();
                Box::pin(async move { cache.playback_buffer().await })
            });
            let permit = ready!(admission.as_mut().poll(cx));
            self.admission = None;
            if permit.is_none() {
                self.retry_fill_at = Some(Instant::now() + self.cache.playback_retry_delay());
            }
            permit.map(|permit| (Vec::with_capacity(BLOCK_BYTES), permit))
        } else {
            None
        };
        self.attempted_fill = Some(block);
        self.fill = buffer.map(|(bytes, permit)| Fill {
            start: block * BLOCK,
            generation: self.gaps.fill_generation(),
            bytes,
            permit,
        });
        Poll::Ready(())
    }
    fn read(&mut self, cx: &mut Context<'_>, buf: &mut ReadBuf<'_>) -> Poll<io::Result<()>> {
        if self.pos >= self.end || buf.remaining() == 0 {
            return Poll::Ready(Ok(()));
        }
        let block = self.pos / BLOCK;
        if self.checked != Some(block) && self.lookup.is_none() {
            let cache = Arc::clone(&self.cache);
            let lease = Arc::clone(&self.lease);
            self.lookup = Some(Box::pin(async move { cache.get(&lease, block).await }));
        }
        if let Some(lookup) = &mut self.lookup {
            self.hit = ready!(lookup.as_mut().poll(cx));
            self.lookup = None;
            self.checked = Some(block);
        }
        let within = (self.pos % BLOCK) as usize;
        let remaining = usize::try_from(self.end - self.pos).unwrap_or(usize::MAX);
        if let Some(data) = &self.hit {
            let n = (data.len() - within).min(buf.remaining()).min(remaining);
            buf.put_slice(&data[within..within + n]);
            self.native_cache_bytes
                .fetch_add(n as u64, Ordering::Relaxed);
            self.backfill.served(n as u64);
            self.pos += n as u64;
            return Poll::Ready(Ok(()));
        }
        if self.source_seeking {
            self.source_pos = ready!(Pin::new(&mut self.inner).poll_complete(cx))?;
            self.source_seeking = false;
        }
        if self.source_pos != self.pos {
            Pin::new(&mut self.inner).start_seek(SeekFrom::Start(self.pos))?;
            self.source_seeking = true;
            self.source_pos = ready!(Pin::new(&mut self.inner).poll_complete(cx))?;
            self.source_seeking = false;
        }
        ready!(self.prepare_fill(cx, block, within));
        let want = buf.remaining().min(BLOCK_BYTES - within).min(remaining);
        let mut source = ReadBuf::new(buf.initialize_unfilled_to(want));
        let outcome = ready!(Pin::new(&mut self.inner).poll_read(cx, &mut source));
        if let Err(error) = outcome {
            self.fill = None;
            return Poll::Ready(Err(error));
        }
        let n = source.filled().len();
        if n == 0 {
            self.fill = None;
            return Poll::Ready(Ok(()));
        }
        if let Some(fill) = &mut self.fill {
            if fill.start + fill.bytes.len() as u64 == self.pos
                && fill.generation == self.gaps.fill_generation()
            {
                fill.bytes.extend_from_slice(source.filled());
            } else {
                self.fill = None;
            }
        }
        self.backfill.served(n as u64);
        self.backfill.miss(self.pos, self.pos + n as u64);
        buf.advance(n);
        self.pos += n as u64;
        self.source_pos += n as u64;
        let whole = usize::try_from(BLOCK.min(self.lease.key().size() - block * BLOCK))
            .unwrap_or(BLOCK_BYTES);
        if self.fill.as_ref().is_some_and(|f| f.bytes.len() == whole)
            && let Some(fill) = self.fill.take()
        {
            self.cache.publish_playback(
                self.lease.clone(),
                block,
                Bytes::from(fill.bytes),
                fill.permit,
            );
        } else if self.pos == self.end
            && let Some(fill) = self.fill.take()
        {
            self.cache
                .remember_playback_prefix(&self.lease, self.pos, fill.bytes, fill.permit);
        }
        Poll::Ready(Ok(()))
    }
}
impl AsyncRead for NativeReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        self.get_mut().read(cx, buf)
    }
}
impl AsyncSeek for NativeReader {
    #[allow(clippy::significant_drop_tightening)] // `this` is a mutable borrow, not a resource guard.
    fn start_seek(self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let this = self.get_mut();
        let pos = match position {
            SeekFrom::Start(pos) => Some(pos),
            SeekFrom::Current(delta) => this.pos.checked_add_signed(delta),
            SeekFrom::End(delta) => this.lease.key().size().checked_add_signed(delta),
        }
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "seek before start of file"))?;
        // Consumers may confirm the current offset before each read. Keeping the
        // same position must retain verified bytes already accumulating in a block.
        if pos == this.pos {
            return Ok(());
        }
        this.pos = pos;
        this.lookup = None;
        this.checked = None;
        this.hit = None;
        this.fill = None;
        this.admission = None;
        this.attempted_fill = None;
        Ok(())
    }
    fn poll_complete(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        Poll::Ready(Ok(self.pos))
    }
}
