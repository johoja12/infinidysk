//! Final-media read-through is shared by FUSE, `WebDAV` and STRM. Cold bytes are
//! returned as they arrive; only complete blocks without any filler are published.
use std::future::Future;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll, ready};

use bytes::Bytes;
use infinidysk_native_cache::{BLOCK, BLOCK_BYTES, Cache, Key, Lease};
use infinidysk_stream::Gaps;
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};
use tokio::sync::OwnedSemaphorePermit;

use crate::Reader;

type Lookup = Pin<Box<dyn Future<Output = Option<Bytes>> + Send>>;
struct Fill {
    start: u64,
    generation: u64,
    bytes: Vec<u8>,
    permit: OwnedSemaphorePermit,
}
pub struct NativeReader {
    inner: Box<Reader>,
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
}
impl NativeReader {
    pub(crate) fn wrap(inner: Reader, cache: Arc<Cache>, key: Key, gaps: Arc<Gaps>) -> Reader {
        let Some(lease) = cache.lease(key) else {
            return inner;
        };
        let end = lease.key().size();
        Reader::Native(Box::new(Self {
            inner: Box::new(inner),
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
        }))
    }
    pub(crate) fn set_end(&mut self, end: u64) {
        self.end = end.min(self.lease.key().size());
        self.inner.set_end(end);
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
        let generation = self.gaps.fill_generation();
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
        if within == 0
            && self.fill.is_none()
            && self.gaps.fill_generation() == generation
            && let Some(permit) = self.cache.try_buffer()
        {
            self.fill = Some(Fill {
                start: self.pos,
                generation,
                bytes: Vec::with_capacity(BLOCK_BYTES),
                permit,
            });
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
        buf.advance(n);
        self.pos += n as u64;
        self.source_pos += n as u64;
        let whole = usize::try_from(BLOCK.min(self.lease.key().size() - block * BLOCK))
            .unwrap_or(BLOCK_BYTES);
        if self.fill.as_ref().is_some_and(|f| f.bytes.len() == whole)
            && let Some(fill) = self.fill.take()
        {
            let cache = Arc::clone(&self.cache);
            let lease = Arc::clone(&self.lease);
            tokio::spawn(async move {
                if let Err(error) = cache
                    .put_reserved(&lease, block, Bytes::from(fill.bytes), fill.permit)
                    .await
                {
                    tracing::debug!(reason=%error,"native cache fill skipped");
                }
            });
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
        this.pos = pos;
        this.lookup = None;
        this.checked = None;
        this.hit = None;
        this.fill = None;
        Ok(())
    }
    fn poll_complete(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        Poll::Ready(Ok(self.pos))
    }
}
