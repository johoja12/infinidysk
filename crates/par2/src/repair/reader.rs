//! Streamed packet framing. The retained window is bounded by one packet, never
//! by the recovery volume's size. A damaged packet cannot hide later valid magic.
use super::{Error, Id, MAX_METADATA, MAX_SCAN, MAX_SLICE, Recovery, RecoverySet};
use crate::{FILE_DESC, HEADER, IFSC, MAGIC, MAIN, RECOVERY, UNICODE_NAME};
use md5::{Digest as _, Md5};
const PIECE: usize = 8192;

struct Framer {
    window: Vec<u8>,
    max_frame: usize,
    scan_limit: usize,
    scanned: usize,
    hashed: usize,
    frames: usize,
    stopped: bool,
    failed: bool,
}
impl Framer {
    fn new(max_frame: usize, scan_limit: usize) -> Result<Self, Error> {
        if scan_limit == 0 || scan_limit > MAX_SCAN {
            return Err(Error::Budget);
        }
        Ok(Self {
            window: Vec::new(),
            max_frame,
            scan_limit,
            scanned: 0,
            hashed: 0,
            frames: 0,
            stopped: false,
            failed: false,
        })
    }
    fn push(
        &mut self,
        bytes: &[u8],
        cancelled: &impl Fn() -> bool,
        accept: &mut impl FnMut(&[u8]) -> Result<bool, Error>,
    ) -> Result<bool, Error> {
        if self.failed {
            return Err(Error::Invalid("failed packet reader"));
        }
        if self.stopped {
            return Ok(false);
        }
        self.failed = true;
        self.scanned = self
            .scanned
            .checked_add(bytes.len())
            .filter(|n| *n <= self.scan_limit)
            .ok_or(Error::Budget)?;
        for piece in bytes.chunks(PIECE) {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            let need = self.window.len() + piece.len();
            if need > self.max_frame + PIECE {
                return Err(Error::Budget);
            }
            if need > self.window.capacity() {
                self.window
                    .try_reserve_exact(need - self.window.len())
                    .map_err(|_| Error::Budget)?;
            }
            self.window.extend_from_slice(piece);
            if !self.scan(false, cancelled, accept)? {
                break;
            }
        }
        self.failed = false;
        Ok(!self.stopped)
    }
    fn finish(
        &mut self,
        cancelled: &impl Fn() -> bool,
        accept: &mut impl FnMut(&[u8]) -> Result<bool, Error>,
    ) -> Result<(), Error> {
        if self.failed {
            return Err(Error::Invalid("failed packet reader"));
        }
        if self.stopped {
            return Ok(());
        }
        self.failed = true;
        self.scan(true, cancelled, accept)?;
        self.stopped = true;
        self.window.clear();
        self.failed = false;
        Ok(())
    }
    fn scan(
        &mut self,
        eof: bool,
        cancelled: &impl Fn() -> bool,
        accept: &mut impl FnMut(&[u8]) -> Result<bool, Error>,
    ) -> Result<bool, Error> {
        let mut at = 0;
        while let Some(relative) = self.window[at..]
            .windows(MAGIC.len())
            .position(|p| p == MAGIC)
        {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            at += relative;
            let Some(header) = self.window.get(at..at + HEADER) else {
                break;
            };
            let length = usize::try_from(u64::from_le_bytes(
                header[8..16].try_into().expect("fixed header"),
            ))
            .unwrap_or(usize::MAX);
            if length < HEADER || !length.is_multiple_of(4) || length > self.max_frame {
                at += 1;
                continue;
            }
            let Some(frame) = self.window.get(at..at + length) else {
                if eof {
                    at += 1;
                    continue;
                }
                break;
            };
            self.hashed = self
                .hashed
                .checked_add(length)
                .filter(|n| *n <= self.scan_limit.saturating_mul(2))
                .ok_or(Error::Budget)?;
            let mut hash = Md5::new();
            for piece in frame[32..].chunks(PIECE) {
                if cancelled() {
                    return Err(Error::Cancelled);
                }
                hash.update(piece);
            }
            if <Id>::from(hash.finalize()) != frame[16..32] {
                at += 1;
                continue;
            }
            self.frames += 1;
            if self.frames > 131_072 {
                return Err(Error::Budget);
            }
            if !accept(frame)? {
                self.stopped = true;
                self.window.clear();
                return Ok(false);
            }
            at += length;
        }
        // Retain incomplete magic/header/frame; otherwise retain only a magic tail.
        let keep = if self.window[at..].starts_with(MAGIC) {
            at
        } else {
            at.max(self.window.len().saturating_sub(MAGIC.len() - 1))
        };
        self.window.drain(..keep);
        Ok(true)
    }
}

/// Collects only checksum-valid core metadata, with a separate 16 MB-scale ceiling.
/// Recovery payloads are framed and discarded. A new reader represents one volume;
/// callers must impose a release-wide scan allowance across volumes as well.
pub struct MetadataReader {
    framer: Framer,
    core: Vec<u8>,
    wanted: Option<Id>,
}
impl MetadataReader {
    pub fn new(wanted: Option<Id>, scan_bytes: usize) -> Result<Self, Error> {
        Ok(Self {
            framer: Framer::new(MAX_METADATA.max(MAX_SLICE + 68), scan_bytes)?,
            core: Vec::new(),
            wanted,
        })
    }
    pub fn push(&mut self, bytes: &[u8], cancelled: impl Fn() -> bool) -> Result<(), Error> {
        let (core, wanted) = (&mut self.core, self.wanted);
        self.framer
            .push(bytes, &cancelled, &mut |frame| collect(core, wanted, frame))?;
        Ok(())
    }
    pub fn finish(mut self, cancelled: impl Fn() -> bool) -> Result<RecoverySet, Error> {
        let (core, wanted) = (&mut self.core, self.wanted);
        self.framer
            .finish(&cancelled, &mut |frame| collect(core, wanted, frame))?;
        RecoverySet::read(&self.core, self.wanted)
    }
}
fn collect(core: &mut Vec<u8>, wanted: Option<Id>, frame: &[u8]) -> Result<bool, Error> {
    if wanted.is_some_and(|id| frame[32..48] != id)
        || ![MAIN.as_slice(), FILE_DESC, IFSC, UNICODE_NAME].contains(&&frame[48..64])
    {
        return Ok(true);
    }
    let length = core
        .len()
        .checked_add(frame.len())
        .filter(|n| *n <= MAX_METADATA)
        .ok_or(Error::Budget)?;
    core.try_reserve_exact(length - core.len())
        .map_err(|_| Error::Budget)?;
    core.extend_from_slice(frame);
    Ok(true)
}

/// Delivers one verified recovery row at a time. Return `false` from the callback
/// once enough independent rows have been retained to stop reading the volume.
/// Reader memory is bounded by `slice_size + 68 + 8192` bytes, plus the row passed
/// to the callback. Account for both in the surrounding reconstruction allowance.
pub struct RecoveryReader {
    framer: Framer,
    set: Id,
    slice_size: usize,
}
impl RecoveryReader {
    pub fn new(set: Id, slice_size: usize, scan_bytes: usize) -> Result<Self, Error> {
        if slice_size == 0 || !slice_size.is_multiple_of(4) || slice_size > MAX_SLICE {
            return Err(Error::Budget);
        }
        Ok(Self {
            framer: Framer::new(slice_size + 68, scan_bytes)?,
            set,
            slice_size,
        })
    }
    pub fn push(
        &mut self,
        bytes: &[u8],
        cancelled: impl Fn() -> bool,
        mut accept: impl FnMut(Recovery) -> Result<bool, Error>,
    ) -> Result<bool, Error> {
        let (set, size) = (self.set, self.slice_size);
        self.framer.push(bytes, &cancelled, &mut |frame| {
            deliver(set, size, frame, &mut accept)
        })
    }
    pub fn finish(
        mut self,
        cancelled: impl Fn() -> bool,
        mut accept: impl FnMut(Recovery) -> Result<bool, Error>,
    ) -> Result<(), Error> {
        let (set, size) = (self.set, self.slice_size);
        self.framer.finish(&cancelled, &mut |frame| {
            deliver(set, size, frame, &mut accept)
        })
    }
}
fn deliver(
    set: Id,
    size: usize,
    frame: &[u8],
    accept: &mut impl FnMut(Recovery) -> Result<bool, Error>,
) -> Result<bool, Error> {
    if frame[32..48] == set && &frame[48..64] == RECOVERY && frame.len() == size + 68 {
        return accept(Recovery {
            set,
            exponent: u32::from_le_bytes(frame[64..68].try_into().expect("fixed exponent")),
            data: frame[68..].to_vec(),
        });
    }
    Ok(true)
}
