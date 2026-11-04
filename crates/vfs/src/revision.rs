//! Covers every delivery and shared-volume dependent, including already-buffered
//! source bytes. A changed repair revision requires reopening the affected reader.
use crate::{CachedMeta, Reader};
use infinidysk_nntp::Client;
use std::io::{self, SeekFrom};
use std::pin::Pin;
use std::sync::Arc;
use std::task::{Context, Poll, ready};
use tokio::io::{AsyncRead, AsyncSeek, ReadBuf};

pub struct RevisionReader {
    inner: Box<Reader>,
    meta: Arc<CachedMeta>,
    client: Arc<Client>,
    fingerprint: [u8; 32],
    generation: u64,
}
impl RevisionReader {
    pub(crate) fn wrap(
        inner: Reader,
        meta: Arc<CachedMeta>,
        client: Arc<Client>,
        fingerprint: [u8; 32],
    ) -> Reader {
        let generation = client.repair_generation();
        if generation == 0 {
            return inner;
        }
        Reader::Revision(Box::new(Self {
            inner: Box::new(inner),
            meta,
            client,
            fingerprint,
            generation: 0,
        }))
    }
    pub(crate) fn set_end(&mut self, end: u64) {
        self.inner.set_end(end);
    }
    fn check(&mut self) -> io::Result<()> {
        let generation = self.client.repair_generation();
        if generation == self.generation {
            return Ok(());
        }
        if !matches!(self.meta.fingerprint(&self.client), Ok(value) if value == self.fingerprint) {
            // Interrupted is retried automatically by read helpers; use InvalidData
            // so a caller must reopen rather than spin on an obsolete handle.
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "source repair revision changed; reopen the file",
            ));
        }
        self.generation = generation;
        Ok(())
    }
}
impl AsyncRead for RevisionReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let this = self.get_mut();
        this.check()?;
        let filled = buf.filled().len();
        let result = ready!(Pin::new(&mut this.inner).poll_read(cx, buf));
        if let Err(error) = this.check() {
            buf.set_filled(filled);
            return Poll::Ready(Err(error));
        }
        Poll::Ready(result)
    }
}
impl AsyncSeek for RevisionReader {
    fn start_seek(self: Pin<&mut Self>, position: SeekFrom) -> io::Result<()> {
        let this = self.get_mut();
        this.check()?;
        Pin::new(&mut this.inner).start_seek(position)
    }
    fn poll_complete(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<u64>> {
        let this = self.get_mut();
        this.check()?;
        let result = ready!(Pin::new(&mut this.inner).poll_complete(cx));
        this.check()?;
        Poll::Ready(result)
    }
}
