//! Actual par2cmdline recovery bytes, independent of the Rust encoder/arithmetic.
use infinidysk_par2::repair::{Error, Limits, Reconstructor, RecoverySet, recovery_packets};
use md5::{Digest as _, Md5};
fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(format!(
        "{}/tests/fixtures/{name}",
        env!("CARGO_MANIFEST_DIR")
    ))
    .expect("fixture")
}
fn data(length: usize) -> Vec<u8> {
    (0..length)
        .map(|i| ((i * 31 + i / 251) % 256).to_le_bytes()[0])
        .collect()
}
fn set() -> RecoverySet {
    RecoverySet::read(&fixture("set.par2"), None).expect("set")
}
fn sources(set: &RecoverySet) -> Vec<Vec<u8>> {
    let files: Vec<_> = set
        .files()
        .iter()
        .map(|file| data(usize::try_from(file.length).expect("file length")))
        .collect();
    set.locations()
        .iter()
        .map(|at| {
            let start = at.local * set.slice_size();
            files[at.file][start..start + at.length].to_vec()
        })
        .collect()
}
#[test]
fn actual_recovery_volume_reconstructs_missing_and_short_slices_in_main_order() {
    for missing in [vec![0], vec![1, 24], vec![0, 24, 26]] {
        let set = set();
        let original = sources(&set);
        let recovery = recovery_packets(
            &fixture("set.vol0+3.par2"),
            set.id(),
            set.slice_size(),
            64,
            16 << 20,
        )
        .expect("packets");
        let mut reconstruction =
            Reconstructor::new(set.clone(), &missing, Limits::default()).expect("plan");
        for packet in recovery {
            reconstruction.offer(packet).expect("independent equation");
        }
        assert!(reconstruction.ready());
        for (index, bytes) in original.iter().enumerate() {
            if !missing.contains(&index) {
                reconstruction
                    .present(index, bytes, || false)
                    .expect("reduce");
            }
        }
        let restored = reconstruction.finish(|| false).expect("decode");
        assert_eq!(restored.len(), missing.len());
        let mut patched = original.clone();
        for slice in restored {
            assert_eq!(slice.data, original[slice.index]);
            patched[slice.index] = slice.data;
        }
        for (index, file) in set.files().iter().enumerate() {
            let mut hash = Md5::new();
            for (location, bytes) in set.locations().iter().zip(&patched) {
                if location.file == index {
                    hash.update(bytes);
                }
            }
            assert_eq!(<[u8; 16]>::from(hash.finalize()), file.hash);
        }
    }
}
#[test]
fn budgets_redundant_equations_and_incomplete_or_poisoned_attempts_fail_closed() {
    let set = set();
    assert!(matches!(
        Reconstructor::new(
            set.clone(),
            &[0, 1, 2],
            Limits {
                missing: 2,
                ..Default::default()
            }
        ),
        Err(Error::Budget)
    ));
    assert!(matches!(
        Reconstructor::new(
            set.clone(),
            &[0],
            Limits {
                memory: 1024,
                ..Default::default()
            }
        ),
        Err(Error::Budget)
    ));
    for missing in [&[0, 0][..], &[1, 0][..], &[32768][..]] {
        assert!(matches!(
            Reconstructor::new(set.clone(), missing, Limits::default()),
            Err(Error::Invalid(_))
        ));
    }
    let packets = recovery_packets(
        &fixture("set.vol0+3.par2"),
        set.id(),
        set.slice_size(),
        64,
        16 << 20,
    )
    .expect("packets");
    let mut recon = Reconstructor::new(set.clone(), &[0, 1], Limits::default()).expect("plan");
    assert!(recon.offer(packets[0].clone()).expect("first"));
    assert!(!recon.offer(packets[0].clone()).expect("redundant"));
    assert!(!recon.ready());
    assert!(matches!(recon.finish(|| false), Err(Error::Incomplete)));
    let mut recon = Reconstructor::new(set.clone(), &[0], Limits::default()).expect("plan");
    recon.offer(packets[0].clone()).expect("equation");
    let original = sources(&set);
    let mut bad = original[1].clone();
    bad[0] ^= 1;
    assert_eq!(recon.present(1, &bad, || false), Err(Error::Source));
    assert!(matches!(recon.finish(|| false), Err(Error::Invalid(_))));
    let mut recon = Reconstructor::new(set, &[0], Limits::default()).expect("plan");
    recon.offer(packets[0].clone()).expect("equation");
    assert_eq!(
        recon.present(1, &original[1], || true),
        Err(Error::Cancelled)
    );
    assert!(matches!(recon.finish(|| false), Err(Error::Invalid(_))));
}
fn packet(set: [u8; 16], kind: &[u8], body: &[u8]) -> Vec<u8> {
    let mut rest = [set.as_slice(), kind, body].concat();
    rest.resize(rest.len().next_multiple_of(4), 0);
    [
        b"PAR2\0PKT".as_slice(),
        &(32 + rest.len() as u64).to_le_bytes(),
        &Md5::digest(&rest),
        &rest,
    ]
    .concat()
}
fn rewrite(
    bytes: &[u8],
    mut change: impl FnMut(&[u8], &[u8]) -> Vec<u8>,
    set: [u8; 16],
) -> Vec<u8> {
    let mut at = 0;
    let mut out = Vec::new();
    while at < bytes.len() {
        let length = usize::try_from(u64::from_le_bytes(
            bytes[at + 8..at + 16].try_into().expect("length"),
        ))
        .expect("fixture length");
        out.extend(packet(
            set,
            &bytes[at + 48..at + 64],
            &change(&bytes[at + 48..at + 64], &bytes[at + 64..at + length]),
        ));
        at += length;
    }
    out
}
#[test]
fn opaque_ids_non_recoverable_files_and_conflicting_metadata_are_handled_explicitly() {
    let raw = fixture("set.par2");
    let opaque = [61; 16];
    let renamed = rewrite(&raw, |_, body| body.to_vec(), opaque);
    let parsed =
        RecoverySet::read(&renamed, Some(opaque)).expect("IDs are opaque per specification");
    assert_eq!(parsed.id(), opaque);
    let extra = [222; 16];
    let main = rewrite(
        &raw,
        |kind, body| {
            let mut body = body.to_vec();
            if kind == b"PAR 2.0\0Main\0\0\0\0" {
                body.extend(extra);
            }
            body
        },
        set().id(),
    );
    let mut extended = main;
    let desc = [
        extra.as_slice(),
        &[0; 32],
        &0u64.to_le_bytes(),
        b"extra.bin",
    ]
    .concat();
    extended.extend(packet(set().id(), b"PAR 2.0\0FileDesc", &desc));
    let parsed = RecoverySet::read(&extended, None).expect("non-recoverable descriptor");
    assert_eq!(parsed.files().len(), 2);
    assert_eq!(parsed.locations().len(), 27);
    let mut conflict = raw.clone();
    let changed = rewrite(
        &raw,
        |kind, body| {
            let mut body = body.to_vec();
            if kind == b"PAR 2.0\0Main\0\0\0\0" {
                body[..8].copy_from_slice(&8192u64.to_le_bytes());
            }
            body
        },
        set().id(),
    );
    conflict.extend(changed);
    assert!(matches!(
        RecoverySet::read(&conflict, None),
        Err(Error::Invalid("conflicting core packets"))
    ));
}
#[test]
fn corrupt_recovery_packets_are_skipped_and_forged_recovery_fails_slice_verification() {
    let set = set();
    let raw = fixture("set.vol0+3.par2");
    let mut damaged = raw.clone();
    let mut at = 0;
    while at < damaged.len() {
        if &damaged[at + 48..at + 64] == b"PAR 2.0\0RecvSlic" {
            damaged[at + 68] ^= 1;
            break;
        }
        at += usize::try_from(u64::from_le_bytes(
            damaged[at + 8..at + 16].try_into().expect("length"),
        ))
        .expect("fixture length");
    }
    assert_eq!(
        recovery_packets(&damaged, set.id(), set.slice_size(), 64, 16 << 20)
            .expect("corrupt packet skipped")
            .len(),
        2
    );
    let forged = rewrite(
        &raw,
        |kind, body| {
            let mut body = body.to_vec();
            if kind == b"PAR 2.0\0RecvSlic" {
                body[4] ^= 1;
            }
            body
        },
        set.id(),
    );
    let packets = recovery_packets(&forged, set.id(), set.slice_size(), 64, 16 << 20)
        .expect("packet hashes valid");
    let mut recon = Reconstructor::new(set.clone(), &[0], Limits::default()).expect("plan");
    for packet in packets {
        recon.offer(packet).expect("equation");
    }
    let original = sources(&set);
    for (index, bytes) in original.iter().enumerate().skip(1) {
        recon
            .present(index, bytes, || false)
            .expect("source verified");
    }
    assert!(matches!(recon.finish(|| false), Err(Error::Verification)));
    assert!(matches!(
        recovery_packets(&raw, set.id(), set.slice_size(), 64, raw.len()),
        Err(Error::Budget)
    ));
}
