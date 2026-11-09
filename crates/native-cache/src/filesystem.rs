//! Platform differences in filesystem counters and device registration.
use std::path::Path;

pub(crate) fn remote(path: &Path) -> Result<bool, nix::Error> {
    let stat = nix::sys::statfs::statfs(path)?;
    #[cfg(target_os = "linux")]
    {
        Ok(matches!(
            stat.filesystem_type().0,
            0x6969 | 0xFF53_4D42 | 0x517B
        ))
    }
    #[cfg(not(target_os = "linux"))]
    {
        Ok(matches!(
            stat.filesystem_type_name(),
            "nfs" | "smbfs" | "cifs" | "afpfs" | "webdav"
        ))
    }
}

// nix exposes block counts as u32 on Darwin and u64 on Linux. Widen before multiplying.
pub(crate) fn bytes(blocks: impl Into<u64>, fragment: u64) -> u64 {
    blocks.into().saturating_mul(fragment)
}

pub(crate) fn device_parts(device: u64) -> (u64, u64) {
    #[cfg(target_os = "macos")]
    {
        // Darwin dev_t uses an 8-bit major and 24-bit minor, as in libc::major/minor.
        ((device >> 24) & 0xff, device & 0x00ff_ffff)
    }
    #[cfg(not(target_os = "macos"))]
    {
        (nix::sys::stat::major(device), nix::sys::stat::minor(device))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capacity_widens_darwin_counts_and_saturates_linux_overflow() {
        assert_eq!(bytes(u32::MAX, 4096), 17_592_186_040_320);
        assert_eq!(bytes(u64::MAX, 4096), u64::MAX);
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn darwin_registration_matches_the_native_device_layout() {
        assert_eq!(device_parts(0xff12_3456), (255, 0x12_3456));
    }
}
