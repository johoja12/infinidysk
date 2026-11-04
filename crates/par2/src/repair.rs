//! Bounded, incremental PAR2 reconstruction. Filesystem/provider I/O and publication
//! belong to the caller. Packet IDs are opaque; packet hashes and slice checksums
//! establish integrity, as required by the PAR 2.0 specification.
use super::{FILE_DESC, File, HEADER, IFSC, Id, Index, MAGIC, MAIN, Packet, RECOVERY, build};
use md5::{Digest as _, Md5};
use std::collections::{BTreeMap, BTreeSet};
use std::sync::OnceLock;
const MAX_INPUT: usize = 32768;
const MAX_SLICE: usize = 32 << 20;
const MAX_METADATA: usize = 16 << 20;
const MAX_SCAN: usize = 512 << 20;
const ORDER: usize = 65535;

#[derive(Debug, Clone, Copy, PartialEq, Eq, thiserror::Error)]
pub enum Error {
    #[error("invalid PAR2 repair metadata: {0}")]
    Invalid(&'static str),
    #[error("PAR2 repair exceeds its memory, slice or scan budget")]
    Budget,
    #[error("not enough independent recovery slices")]
    Recovery,
    #[error("source slice does not match PAR2 checksums")]
    Source,
    #[error("reconstructed slice does not match PAR2 checksums")]
    Verification,
    #[error("PAR2 reconstruction has not received every present slice")]
    Incomplete,
    #[error("PAR2 reconstruction cancelled")]
    Cancelled,
}
#[derive(Clone, Copy, Debug)]
pub struct Location {
    pub file: usize,
    pub local: usize,
    pub length: usize,
}
#[derive(Clone)]
pub struct RecoverySet {
    id: Id,
    slice_size: usize,
    files: Vec<File>,
    locations: Vec<Location>,
    heap: usize,
}
impl RecoverySet {
    /// Reads checksum-valid core packets. Duplicate conflicting core packets and
    /// incomplete recoverable-file descriptions cannot become reconstruction input.
    pub fn read(buf: &[u8], wanted: Option<Id>) -> Result<Self, Error> {
        if buf.len() > MAX_METADATA {
            return Err(Error::Budget);
        }
        let packets: Vec<_> =
            packets(buf, MAX_METADATA.max(MAX_SLICE + 68)).collect::<Result<_, _>>()?;
        let main = packets
            .iter()
            .find(|p| p.kind == MAIN && p.body.len() >= 12 && wanted.is_none_or(|id| p.set == id))
            .ok_or(Error::Invalid("main packet unavailable"))?;
        let id = main.set;
        let mut unique: BTreeMap<([u8; 16], Id), &[u8]> = BTreeMap::new();
        for packet in packets.iter().filter(|p| p.set == id) {
            if ![MAIN.as_slice(), FILE_DESC, IFSC].contains(&packet.kind) {
                continue;
            }
            let key = if packet.kind == MAIN {
                id
            } else {
                packet
                    .body
                    .get(..16)
                    .and_then(|b| b.try_into().ok())
                    .ok_or(Error::Invalid("short core packet"))?
            };
            let kind = packet
                .kind
                .try_into()
                .map_err(|_| Error::Invalid("packet type"))?;
            if unique
                .insert((kind, key), packet.body)
                .is_some_and(|old| old != packet.body)
            {
                return Err(Error::Invalid("conflicting core packets"));
            }
            if packet.kind == IFSC && !(packet.body.len() - 16).is_multiple_of(20) {
                return Err(Error::Invalid("incomplete slice checksums"));
            }
        }
        let size = u64::from_le_bytes(main.body[..8].try_into().expect("fixed header"));
        let slice_size = usize::try_from(size).map_err(|_| Error::Budget)?;
        if slice_size == 0 || !slice_size.is_multiple_of(4) || slice_size > MAX_SLICE {
            return Err(Error::Budget);
        }
        let count = u32::from_le_bytes(main.body[8..12].try_into().expect("fixed header")) as usize;
        let (ids, tail) = main.body[12..].as_chunks::<16>();
        if !tail.is_empty()
            || count == 0
            || count > ids.len()
            || count > MAX_INPUT
            || ids.iter().collect::<BTreeSet<_>>().len() != ids.len()
        {
            return Err(Error::Invalid("recoverable file order"));
        }
        let selected: Vec<_> = packets.into_iter().filter(|p| p.set == id).collect();
        let mut index = Index::default();
        build(&mut index, &selected);
        let mut files = Vec::new();
        let mut locations = Vec::new();
        let mut heap = 0usize;
        for wanted in &ids[..count] {
            let file = index
                .files
                .iter()
                .find(|f| f.id == *wanted)
                .ok_or(Error::Invalid("file description unavailable"))?;
            let slices = usize::try_from(file.length.div_ceil(size)).map_err(|_| Error::Budget)?;
            if slices != file.slices.len() {
                return Err(Error::Invalid("slice checksums unavailable"));
            }
            if slices > MAX_INPUT - locations.len() {
                return Err(Error::Budget);
            }
            for local in 0..slices {
                let remaining =
                    file.length - u64::try_from(local).map_err(|_| Error::Budget)? * size;
                locations.push(Location {
                    file: files.len(),
                    local,
                    length: usize::try_from(remaining.min(size)).map_err(|_| Error::Budget)?,
                });
            }
            heap = heap
                .checked_add(
                    file.name.len() + file.slices.len() * std::mem::size_of::<super::Slice>(),
                )
                .ok_or(Error::Budget)?;
            files.push(file.clone());
        }
        if locations.is_empty() {
            return Err(Error::Invalid("empty recovery set"));
        }
        heap = heap
            .checked_add(files.capacity() * std::mem::size_of::<File>())
            .and_then(|heap| {
                heap.checked_add(locations.capacity() * std::mem::size_of::<Location>())
            })
            .ok_or(Error::Budget)?;
        Ok(Self {
            id,
            slice_size,
            files,
            locations,
            heap,
        })
    }
    #[must_use]
    pub const fn id(&self) -> Id {
        self.id
    }
    #[must_use]
    pub const fn slice_size(&self) -> usize {
        self.slice_size
    }
    #[must_use]
    pub fn files(&self) -> &[File] {
        &self.files
    }
    #[must_use]
    pub fn locations(&self) -> &[Location] {
        &self.locations
    }
}

/// Only checksum-valid, exact-set, exact-length packets construct this value.
#[derive(Clone)]
pub struct Recovery {
    set: Id,
    exponent: u32,
    data: Vec<u8>,
}
impl Recovery {
    #[must_use]
    pub const fn exponent(&self) -> u32 {
        self.exponent
    }
}
/// Bounded buffer adapter; a streaming transport may pass bounded packet windows.
/// `memory` accounts for the supplied buffer and retained recovery payloads together.
pub fn recovery_packets(
    buf: &[u8],
    set: Id,
    slice_size: usize,
    limit: usize,
    memory: usize,
) -> Result<Vec<Recovery>, Error> {
    if buf.len() > MAX_SCAN
        || buf.len() > memory
        || !(1..=64).contains(&limit)
        || slice_size == 0
        || slice_size > MAX_SLICE
    {
        return Err(Error::Budget);
    }
    let mut bytes = buf
        .len()
        .checked_add(limit * std::mem::size_of::<Recovery>())
        .filter(|bytes| *bytes <= memory)
        .ok_or(Error::Budget)?;
    let mut out = Vec::with_capacity(limit);
    for packet in packets(buf, MAX_METADATA.max(slice_size + 68)) {
        let packet = packet?;
        if packet.set != set || packet.kind != RECOVERY || packet.body.len() != slice_size + 4 {
            continue;
        }
        bytes = bytes
            .checked_add(slice_size)
            .filter(|b| *b <= memory)
            .ok_or(Error::Budget)?;
        out.push(Recovery {
            set,
            exponent: u32::from_le_bytes(packet.body[..4].try_into().expect("fixed exponent")),
            data: packet.body[4..].to_vec(),
        });
        if out.len() == limit {
            break;
        }
    }
    Ok(out)
}
fn packets(buf: &[u8], max_frame: usize) -> impl Iterator<Item = Result<Packet<'_>, Error>> {
    let mut at = 0;
    let mut hashed = 0usize;
    let mut accepted = 0;
    std::iter::from_fn(move || {
        loop {
            let start = buf.get(at..)?.windows(8).position(|p| p == MAGIC)? + at;
            at = start + 1;
            let header = buf.get(start..start + HEADER)?;
            let Ok(len) = usize::try_from(u64::from_le_bytes(
                header[8..16].try_into().expect("fixed length"),
            )) else {
                continue;
            };
            if len < HEADER || !len.is_multiple_of(4) || len > max_frame {
                continue;
            }
            let Some(frame) = start.checked_add(len).and_then(|end| buf.get(start..end)) else {
                continue;
            };
            let Some(work) = hashed
                .checked_add(len)
                .filter(|bytes| *bytes <= buf.len().saturating_mul(2))
            else {
                at = buf.len();
                return Some(Err(Error::Budget));
            };
            hashed = work;
            if <Id>::from(Md5::digest(&frame[32..])) != header[16..32] {
                continue;
            }
            if accepted >= 131_072 {
                at = buf.len();
                return Some(Err(Error::Budget));
            }
            accepted += 1;
            at = start + len;
            return Some(Ok(Packet {
                set: header[32..48].try_into().expect("fixed set"),
                kind: &header[48..64],
                body: &frame[HEADER..],
            }));
        }
    })
}

struct Field {
    log: Vec<usize>,
    exp: Vec<u16>,
    input: Vec<usize>,
}
impl Field {
    fn shared() -> &'static Self {
        static FIELD: OnceLock<Field> = OnceLock::new();
        FIELD.get_or_init(|| {
            let mut log = vec![0; 65536];
            let mut exp = vec![0; ORDER * 2];
            let mut value = 1u32;
            for i in 0..ORDER {
                exp[i] = u16::try_from(value).expect("GF16");
                exp[i + ORDER] = exp[i];
                log[value as usize] = i;
                value <<= 1;
                if value & 0x10000 != 0 {
                    value ^= 0x1100B;
                }
            }
            let input = (1..ORDER)
                .filter(|v| [3, 5, 17, 257].iter().all(|factor| v % factor != 0))
                .collect();
            Self { log, exp, input }
        })
    }
    fn mul(&self, a: u16, b: u16) -> u16 {
        if a == 0 || b == 0 {
            0
        } else {
            self.exp[self.log[usize::from(a)] + self.log[usize::from(b)]]
        }
    }
    fn inv(&self, value: u16) -> u16 {
        self.exp[ORDER - self.log[usize::from(value)]]
    }
    fn coefficient(&self, index: usize, exponent: u32) -> u16 {
        let power = self.input[index] as u64 * u64::from(exponent) % ORDER as u64;
        self.exp[usize::try_from(power).expect("GF16 exponent is below 65535")]
    }
}
#[derive(Clone, Copy)]
pub struct Limits {
    pub missing: usize,
    pub memory: usize,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            missing: 8,
            memory: 256 << 20,
        }
    }
}
pub struct Recovered {
    pub index: usize,
    pub data: Vec<u8>,
}
/// Retains only independent recovery rows and one residual per missing slice.
/// Callers must run CPU work outside async runtime threads. Any failed reduction
/// poisons this attempt; partial arithmetic can never be published.
pub struct Reconstructor {
    set: RecoverySet,
    missing: Vec<usize>,
    matrix: Vec<Vec<u16>>,
    basis: Vec<(usize, Vec<u16>)>,
    exponents: Vec<u32>,
    residuals: Vec<Vec<u8>>,
    next: usize,
    started: bool,
    poisoned: bool,
}
impl Reconstructor {
    pub fn new(set: RecoverySet, missing: &[usize], limits: Limits) -> Result<Self, Error> {
        if missing.is_empty()
            || missing.windows(2).any(|p| p[0] >= p[1])
            || missing.iter().any(|i| *i >= set.locations.len())
        {
            return Err(Error::Invalid("missing slice order"));
        }
        let k = missing.len();
        let need = (k + 2)
            .checked_mul(set.slice_size)
            .and_then(|n| n.checked_add(set.heap))
            .and_then(|n| n.checked_add((2 << 20) + k * k * 8))
            .ok_or(Error::Budget)?;
        if k > limits.missing || k > 64 || need > limits.memory {
            return Err(Error::Budget);
        }
        Ok(Self {
            set,
            missing: missing.to_vec(),
            matrix: Vec::new(),
            basis: Vec::new(),
            exponents: Vec::new(),
            residuals: Vec::new(),
            next: 0,
            started: false,
            poisoned: false,
        })
    }
    #[must_use]
    pub fn ready(&self) -> bool {
        self.matrix.len() == self.missing.len()
    }
    pub fn offer(&mut self, recovery: Recovery) -> Result<bool, Error> {
        if self.started || self.poisoned {
            return Err(Error::Invalid("recovery arrived after reduction"));
        }
        if recovery.set != self.set.id || recovery.data.len() != self.set.slice_size {
            return Err(Error::Invalid("recovery geometry"));
        }
        if self.ready() {
            return Ok(false);
        }
        let field = Field::shared();
        let coefficients: Vec<_> = self
            .missing
            .iter()
            .map(|i| field.coefficient(*i, recovery.exponent))
            .collect();
        let mut reduced = coefficients.clone();
        for (pivot, row) in &self.basis {
            let factor = reduced[*pivot];
            for (v, known) in reduced.iter_mut().zip(row) {
                *v ^= field.mul(factor, *known);
            }
        }
        let Some(pivot) = reduced.iter().position(|v| *v != 0) else {
            return Ok(false);
        };
        let inverse = field.inv(reduced[pivot]);
        for value in &mut reduced {
            *value = field.mul(*value, inverse);
        }
        let position = self.basis.partition_point(|(p, _)| *p < pivot);
        self.basis.insert(position, (pivot, reduced));
        self.matrix.push(coefficients);
        self.exponents.push(recovery.exponent);
        self.residuals.push(recovery.data);
        Ok(true)
    }
    pub fn present(
        &mut self,
        index: usize,
        data: &[u8],
        cancelled: impl Fn() -> bool,
    ) -> Result<(), Error> {
        if self.poisoned {
            return Err(Error::Invalid("failed attempt"));
        }
        self.poisoned = true;
        if !self.ready() {
            return Err(Error::Recovery);
        }
        self.skip_missing();
        if index != self.next {
            return Err(Error::Invalid("source slice order"));
        }
        let location = self
            .set
            .locations
            .get(index)
            .ok_or(Error::Invalid("source slice index"))?;
        if data.len() != location.length
            || !self.set.files[location.file].slices[location.local]
                .matches(self.set.slice_size as u64, data)
        {
            return Err(Error::Source);
        }
        self.started = true;
        for (exponent, residual) in self.exponents.iter().zip(&mut self.residuals) {
            xor_scaled(
                residual,
                data,
                Field::shared().coefficient(index, *exponent),
                &cancelled,
            )?;
        }
        self.next += 1;
        self.poisoned = false;
        Ok(())
    }
    fn skip_missing(&mut self) {
        while self.missing.binary_search(&self.next).is_ok() {
            self.next += 1;
        }
    }
    pub fn finish(mut self, cancelled: impl Fn() -> bool) -> Result<Vec<Recovered>, Error> {
        if self.poisoned {
            return Err(Error::Invalid("failed attempt"));
        }
        self.skip_missing();
        if self.next != self.set.locations.len() {
            return Err(Error::Incomplete);
        }
        if !self.ready() {
            return Err(Error::Recovery);
        }
        let field = Field::shared();
        let k = self.missing.len();
        for column in 0..k {
            if cancelled() {
                return Err(Error::Cancelled);
            }
            let pivot = (column..k)
                .find(|r| self.matrix[*r][column] != 0)
                .ok_or(Error::Recovery)?;
            self.matrix.swap(column, pivot);
            self.residuals.swap(column, pivot);
            let inverse = field.inv(self.matrix[column][column]);
            for value in &mut self.matrix[column] {
                *value = field.mul(*value, inverse);
            }
            scale(&mut self.residuals[column], inverse, &cancelled)?;
            let coefficients = self.matrix[column].clone();
            for row in 0..k {
                if row == column {
                    continue;
                }
                let factor = self.matrix[row][column];
                for (value, pivot) in self.matrix[row].iter_mut().zip(&coefficients) {
                    *value ^= field.mul(factor, *pivot);
                }
                let (dest, source) = if row < column {
                    let (left, right) = self.residuals.split_at_mut(column);
                    (&mut left[row], &right[0])
                } else {
                    let (left, right) = self.residuals.split_at_mut(row);
                    (&mut right[0], &left[column])
                };
                xor_scaled(dest, source, factor, &cancelled)?;
            }
        }
        let mut out = Vec::with_capacity(k);
        for (index, mut data) in self.missing.into_iter().zip(self.residuals) {
            let location = self.set.locations[index];
            if !self.set.files[location.file].slices[location.local]
                .matches(self.set.slice_size as u64, &data)
            {
                return Err(Error::Verification);
            }
            data.truncate(location.length);
            out.push(Recovered { index, data });
        }
        Ok(out)
    }
}
fn xor_scaled(
    dest: &mut [u8],
    source: &[u8],
    factor: u16,
    cancelled: &impl Fn() -> bool,
) -> Result<(), Error> {
    if factor == 0 {
        return Ok(());
    }
    let field = Field::shared();
    for (block, chunk) in source.chunks(8192).enumerate() {
        if cancelled() {
            return Err(Error::Cancelled);
        }
        let at = block * 8192;
        if factor == 1 {
            for (d, s) in dest[at..].iter_mut().zip(chunk) {
                *d ^= *s;
            }
            continue;
        }
        for (word, pair) in chunk.chunks(2).enumerate() {
            let value = field
                .mul(
                    u16::from_le_bytes([pair[0], pair.get(1).copied().unwrap_or(0)]),
                    factor,
                )
                .to_le_bytes();
            dest[at + word * 2] ^= value[0];
            dest[at + word * 2 + 1] ^= value[1];
        }
    }
    Ok(())
}
fn scale(bytes: &mut [u8], factor: u16, cancelled: &impl Fn() -> bool) -> Result<(), Error> {
    let field = Field::shared();
    for chunk in bytes.chunks_mut(8192) {
        if cancelled() {
            return Err(Error::Cancelled);
        }
        for word in chunk.as_chunks_mut::<2>().0 {
            *word = field.mul(u16::from_le_bytes(*word), factor).to_le_bytes();
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn gf16_inverse_and_input_constants_cover_the_field() {
        let f = Field::shared();
        assert_eq!(f.input.len(), MAX_INPUT);
        assert_eq!(&f.input[..8], &[1, 2, 4, 7, 8, 11, 13, 14]);
        for value in 1..=u16::MAX {
            assert_eq!(f.mul(value, f.inv(value)), 1);
        }
    }
}
