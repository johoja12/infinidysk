//! Ordered damage census. An intact file needs both IFSC and whole-file proof;
//! damaged files defer their whole-file proof until reconstruction is complete.
use super::{Error, Id, RecoverySet};
use md5::{Digest as _, Md5};
pub struct Inspection {
    set: RecoverySet,
    maximum: usize,
    missing: Vec<usize>,
    next: usize,
    file: usize,
    whole: Md5,
    damaged: bool,
    failed: bool,
}
impl Inspection {
    pub fn new(set: RecoverySet, maximum: usize) -> Result<Self, Error> {
        if !(1..=64).contains(&maximum) {
            return Err(Error::Budget);
        }
        Ok(Self {
            set,
            maximum,
            missing: Vec::new(),
            next: 0,
            file: 0,
            whole: Md5::new(),
            damaged: false,
            failed: false,
        })
    }
    #[must_use]
    pub fn missing_count(&self) -> usize {
        self.missing.len()
    }
    pub fn slice(
        &mut self,
        index: usize,
        bytes: Option<&[u8]>,
        cancelled: impl Fn() -> bool,
    ) -> Result<(), Error> {
        if self.failed {
            return Err(Error::Invalid("failed inspection"));
        }
        self.failed = true;
        if index != self.next {
            return Err(Error::Invalid("inspection slice order"));
        }
        let at = *self
            .set
            .locations
            .get(index)
            .ok_or(Error::Invalid("inspection slice index"))?;
        while self.file < at.file {
            self.finish_file()?;
        }
        if cancelled() {
            return Err(Error::Cancelled);
        }
        if let Some(bytes) = bytes.filter(|data| {
            data.len() == at.length
                && self.set.files[at.file].slices[at.local]
                    .matches(self.set.slice_size as u64, data)
        }) {
            for piece in bytes.chunks(8192) {
                if cancelled() {
                    return Err(Error::Cancelled);
                }
                self.whole.update(piece);
            }
        } else {
            self.damaged = true;
            self.missing.push(index);
            if self.missing.len() > self.maximum {
                return Err(Error::Budget);
            }
        }
        self.next += 1;
        self.failed = false;
        Ok(())
    }
    fn finish_file(&mut self) -> Result<(), Error> {
        let hash = std::mem::take(&mut self.whole).finalize();
        if !self.damaged && <Id>::from(hash) != self.set.files[self.file].hash {
            return Err(Error::Verification);
        }
        self.file += 1;
        self.damaged = false;
        Ok(())
    }
    pub fn finish(mut self, cancelled: impl Fn() -> bool) -> Result<Vec<usize>, Error> {
        if self.failed {
            return Err(Error::Invalid("failed inspection"));
        }
        if self.next != self.set.locations.len() {
            return Err(Error::Incomplete);
        }
        while self.file < self.set.files.len() {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            self.finish_file()?;
        }
        Ok(self.missing)
    }
}
