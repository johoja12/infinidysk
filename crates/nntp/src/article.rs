use std::str::FromStr;

use bytes::Bytes;

/// Upper bound on one decoded article; real articles are around 1 MiB.
pub const MAX_DECODED_LEN: usize = 32 * 1024 * 1024;

/// A decoded yEnc article body.
#[derive(Debug, Clone)]
pub struct Body {
    pub data: Bytes,
    /// Zero-based offset of `data` within the file.
    pub offset: u64,
    /// Size of the whole file from `=ybegin size=`.
    pub file_size: u64,
    /// One-based part number, for multipart posts.
    pub part: Option<u32>,
    /// Number of parts the file was posted in, from `=ybegin total=`.
    pub total: Option<u32>,
}

/// Geometry independently known from the imported source. Checked before a body
/// can reach a caller or the abandoned-body cache sink.
#[derive(Clone, Copy, Debug, Default)]
pub struct BodyExpectation {
    pub part: Option<u32>,
    pub total: Option<u32>,
    pub file_size: Option<u64>,
    pub offset: Option<u64>,
    pub decoded_len: Option<u64>,
}
impl BodyExpectation {
    pub(crate) fn validate(self, body: &Body) -> Result<(), crate::Error> {
        let regular = match body.part {
            Some(1) if body.offset == 0 => Some(body.data.len() as u64),
            Some(part)
                if part > 1
                    && body.offset > 0
                    && body.offset.is_multiple_of(u64::from(part - 1)) =>
            {
                Some(body.offset / u64::from(part - 1))
            }
            _ => None,
        };
        let obfuscated_total = self.part == body.part
            && regular.is_some_and(|size| {
                size > 0
                    && body.data.len() as u64 <= size
                    && body
                        .total
                        .is_some_and(|total| body.file_size.div_ceil(size) != u64::from(total))
            });
        let impossible_range = self.part.is_some()
            && body.part.is_some()
            && self.offset.is_none()
            && match self.part {
                Some(1) => body.offset != 0,
                Some(_) => {
                    body.offset == 0
                        || (body.file_size > 0
                            && body.offset.saturating_add(body.data.len() as u64) > body.file_size)
                }
                None => false,
            };
        if self
            .part
            .is_some_and(|v| body.part.is_some_and(|actual| actual != v))
            || impossible_range
            || (!obfuscated_total
                && self
                    .total
                    .is_some_and(|v| body.total.is_some_and(|actual| actual != v)))
            || self.file_size.is_some_and(|v| body.file_size != v)
            || self.offset.is_some_and(|v| body.offset != v)
            || self
                .decoded_len
                .is_some_and(|v| body.data.len() as u64 != v)
        {
            return Err(crate::Error::Yenc(
                "article does not match imported geometry",
            ));
        }
        Ok(())
    }
}

/// Value of `key=` in a `=y…` header line. Stops at `name=`, which is always last and
/// may contain spaces.
fn raw_field<'a>(line: &'a [u8], key: &[u8]) -> Option<&'a str> {
    let value = line
        .trim_ascii_end()
        .split(|&b| b == b' ')
        .take_while(|token| !token.starts_with(b"name="))
        .find_map(|token| token.strip_prefix(key)?.strip_prefix(b"="))?;
    std::str::from_utf8(value).ok()
}

pub fn field<T: FromStr>(line: &[u8], key: &[u8]) -> Option<T> {
    raw_field(line, key)?.parse().ok()
}

pub fn crc_field(line: &[u8], key: &[u8]) -> Option<u32> {
    u32::from_str_radix(raw_field(line, key)?, 16).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fields_stop_at_name() {
        let line = b"=ybegin part=2 line=128 size=15 name=a size=9 b.bin\r\n";
        assert_eq!(field::<u64>(line, b"size"), Some(15));
        assert_eq!(field::<u32>(line, b"part"), Some(2));
        assert_eq!(
            crc_field(b"end size=5 pcrc32=0aBc\r\n", b"pcrc32"),
            Some(0xabc)
        );
        assert_eq!(crc_field(b"end size=5 pcrc32=0aBc\r\n", b"crc32"), None);
    }
}
