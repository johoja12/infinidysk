//! Persistent proof binding an external decoded cache to its repair ledger.
use nix::fcntl::{Flock, FlockArg, OFlag, openat, renameat};
use nix::sys::stat::Mode;
use nix::unistd::{UnlinkatFlags, unlinkat};
use sha2::{Digest as _, Sha256};
use std::fs::{File, OpenOptions};
use std::io::{self, Read as _, Write as _};
use std::os::unix::fs::{MetadataExt as _, OpenOptionsExt as _};
use std::path::Path;
const FILE: &str = ".repair-lineage";
const MAGIC: &[u8; 8] = b"IDRL\0\0\0\x01";
fn invalid() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "segment cache belongs to a different or newer repair ledger; restore matching repair state",
    )
}
/// Synchronous and fsynced; called only from a blocking worker holding the disk's
/// owned lineage guard. The OS lock also serializes other processes' bind attempts.
pub(super) fn bind(path: &Path, suffix: &str, id: [u8; 16], epoch: u64) -> io::Result<()> {
    let root = OpenOptions::new()
        .read(true)
        .custom_flags((OFlag::O_DIRECTORY | OFlag::O_NOFOLLOW).bits())
        .open(path)?;
    let lock = File::from(openat(
        &root,
        ".repair-lineage.lock",
        OFlag::O_RDWR | OFlag::O_CREAT | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::S_IRUSR | Mode::S_IWUSR,
    )?);
    if !lock.metadata()?.is_file() || lock.metadata()?.nlink() != 1 {
        return Err(invalid());
    }
    let _lock = Flock::lock(lock, FlockArg::LockExclusiveNonblock)
        .map_err(|(_, error)| io::Error::from(error))?;
    match openat(
        &root,
        FILE,
        OFlag::O_RDONLY | OFlag::O_NOFOLLOW | OFlag::O_NONBLOCK,
        Mode::empty(),
    ) {
        Ok(fd) => {
            let mut file = File::from(fd);
            let meta = file.metadata()?;
            if !meta.is_file() || meta.nlink() != 1 || meta.len() != 64 {
                return Err(invalid());
            }
            let mut record = [0; 64];
            file.read_exact(&mut record)?;
            if &record[..8] != MAGIC
                || record[8..24] != id
                || <[u8; 32]>::from(Sha256::digest(&record[..32])) != record[32..]
            {
                return Err(invalid());
            }
            let old = u64::from_le_bytes(record[24..32].try_into().expect("fixed epoch"));
            if old > epoch {
                return Err(invalid());
            }
            if old == epoch {
                return Ok(());
            }
        }
        Err(nix::errno::Errno::ENOENT) => {}
        Err(error) => return Err(error.into()),
    }
    let mut record = Vec::with_capacity(64);
    record.extend(MAGIC);
    record.extend(id);
    record.extend(epoch.to_le_bytes());
    record.extend(Sha256::digest(&record));
    let temp = format!(".repair-lineage{suffix}");
    let mut file = File::from(openat(
        &root,
        temp.as_str(),
        OFlag::O_WRONLY | OFlag::O_CREAT | OFlag::O_EXCL | OFlag::O_NOFOLLOW,
        Mode::S_IRUSR | Mode::S_IWUSR,
    )?);
    let result = (|| {
        file.write_all(&record)?;
        file.sync_all()?;
        renameat(&root, temp.as_str(), &root, FILE)?;
        root.sync_all()
    })();
    if result.is_err() {
        let _ = unlinkat(&root, temp.as_str(), UnlinkatFlags::NoRemoveDir);
    }
    result
}
