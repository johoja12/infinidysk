//! Final publication proof. No recovered slice leaves this gate until every
//! recoverable file has also passed its whole-file MD5 check.
use super::{Error, Id, Recovered, RecoverySet};
use md5::{Digest as _, Md5};

pub struct Verification {
    set: RecoverySet,
    recovered: Vec<Recovered>,
    next: usize,
    file: usize,
    whole: Md5,
    failed: bool,
}
/// Verified slice bytes and their exact PAR2 set. Construction is private so the
/// publishing caller cannot confuse IFSC-only recovery with whole-file proof.
pub struct VerifiedRepair {
    set: Id,
    recovered: Vec<Recovered>,
}
impl VerifiedRepair {
    #[must_use]
    pub const fn set(&self) -> Id {
        self.set
    }
    #[must_use]
    pub fn slices(&self) -> &[Recovered] {
        &self.recovered
    }
    #[must_use]
    pub fn into_slices(self) -> Vec<Recovered> {
        self.recovered
    }
}
impl Verification {
    pub fn new(set: RecoverySet, recovered: Vec<Recovered>) -> Result<Self, Error> {
        if recovered.is_empty() || recovered.windows(2).any(|p| p[0].index >= p[1].index) {
            return Err(Error::Invalid("recovered slice order"));
        }
        for slice in &recovered {
            let at = set
                .locations
                .get(slice.index)
                .ok_or(Error::Invalid("recovered slice index"))?;
            if slice.data.len() != at.length
                || !set.files[at.file].slices[at.local].matches(set.slice_size as u64, &slice.data)
            {
                return Err(Error::Verification);
            }
        }
        Ok(Self {
            set,
            recovered,
            next: 0,
            file: 0,
            whole: Md5::new(),
            failed: false,
        })
    }
    /// Ordered final read pass. `source` is unnecessary for a recovered slice;
    /// otherwise it must be the current bytes from the bound source/range.
    /// Any failure poisons the gate and prevents later publication.
    pub fn slice(
        &mut self,
        index: usize,
        source: Option<&[u8]>,
        cancelled: impl Fn() -> bool,
    ) -> Result<(), Error> {
        if self.failed {
            return Err(Error::Invalid("failed verification"));
        }
        self.failed = true;
        if index != self.next {
            return Err(Error::Invalid("verification slice order"));
        }
        let at = *self
            .set
            .locations
            .get(index)
            .ok_or(Error::Invalid("verification slice index"))?;
        // Empty recoverable files contribute no locations but still require MD5 proof.
        while self.file < at.file {
            self.finish_file()?;
        }
        let bytes = match self.recovered.binary_search_by_key(&index, |s| s.index) {
            Ok(i) => self.recovered[i].data.as_slice(),
            Err(_) => source.ok_or(Error::Source)?,
        };
        if bytes.len() != at.length
            || !self.set.files[at.file].slices[at.local].matches(self.set.slice_size as u64, bytes)
        {
            return Err(Error::Source);
        }
        for piece in bytes.chunks(8192) {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            self.whole.update(piece);
        }
        self.next += 1;
        self.failed = false;
        Ok(())
    }
    fn finish_file(&mut self) -> Result<(), Error> {
        if <Id>::from(std::mem::take(&mut self.whole).finalize()) != self.set.files[self.file].hash
        {
            return Err(Error::Verification);
        }
        self.file += 1;
        Ok(())
    }
    pub fn finish(mut self, cancelled: impl Fn() -> bool) -> Result<VerifiedRepair, Error> {
        if self.failed {
            return Err(Error::Invalid("failed verification"));
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
        Ok(VerifiedRepair {
            set: self.set.id,
            recovered: self.recovered,
        })
    }
}
