//! Authoritative repair-data bootstrap. Disabling future attempts cannot disable
//! already published articles or silently create a replacement for a lost ledger.
use anyhow::{Context as _, bail};
use infinidysk_db::Db;
use infinidysk_native_cache::{
    Cache as NativeCache,
    repair::{Error, Store, Witness},
};
use infinidysk_stream::Cache;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;
const OPEN_WAIT: Duration = Duration::from_secs(10);
const WITNESS_WAIT: Duration = Duration::from_secs(5);
/// The repair path is stable under the application's persistent config directory;
/// cache retention and cache-folder moves do not move authoritative repaired data.
pub async fn open(
    config_dir: &Path,
    db: Db,
    decoded: Arc<Cache>,
    native: Option<Arc<NativeCache>>,
    create: bool,
) -> anyhow::Result<Option<Arc<Store>>> {
    let path = config_dir.join("repair-v2");
    let binding = db
        .repair_lineage()
        .await
        .context("reading the repair ledger binding")?;
    let native_binding = if let Some(native) = &native {
        native
            .repair_lineage()
            .await
            .context("reading the native cache repair witness")?
    } else {
        None
    };
    let decoded_binding = tokio::time::timeout(OPEN_WAIT, decoded.repair_lineage())
        .await
        .context("decoded cache repair witness read timed out")?
        .context("reading the decoded cache repair witness")?
        .map(|(id, epoch)| (uuid::Uuid::from_bytes(id), epoch));
    let witnesses = [
        binding.map(|b| (b.id, b.epoch)),
        native_binding,
        decoded_binding,
    ];
    let database = tokio::fs::try_exists(path.join("catalogue.sqlite"))
        .await
        .context("checking the repair data directory")?;
    let anchor = tokio::fs::try_exists(path.join("repair-anchor.json")).await?;
    if !create && witnesses.iter().all(Option::is_none) && !database && !anchor {
        return Ok(None);
    }
    if witnesses.iter().any(Option::is_some) && !database {
        bail!(
            "authoritative repair data is missing; restore the matching repair-v2 directory before starting"
        );
    }
    let opening = tokio::task::spawn_blocking(move || {
        tokio::runtime::Handle::current().block_on(Store::open(&path))
    });
    let store = tokio::time::timeout(OPEN_WAIT, opening)
        .await
        .context("repair ledger startup timed out")?
        .context("repair ledger startup worker failed")?
        .context("opening authoritative repair data")?;
    if witnesses
        .into_iter()
        .flatten()
        .any(|(id, epoch)| id != store.id() || epoch > store.epoch())
    {
        bail!(
            "repair data does not match its database/cache witnesses; restore the matching repair-v2 directory"
        );
    }
    let witness: Witness = Arc::new(move |id, epoch| {
        let (db, decoded, native) = (db.clone(), decoded.clone(), native.clone());
        Box::pin(async move {
            tokio::time::timeout(WITNESS_WAIT, async {
                if !db
                    .bind_repair_lineage(id, epoch)
                    .await
                    .map_err(|_| Error::Witness)?
                {
                    return Err(Error::Invalid("main database repair lineage changed or regressed"));
                }
                if let Some(native) = native {
                    native.bind_repair_lineage(id, epoch).await.map_err(|error| match error {
                        infinidysk_native_cache::Error::Invalid(reason) => Error::Invalid(reason),
                        _ => Error::Witness,
                    })?;
                }
                decoded
                    .bind_repair_lineage(*id.as_bytes(), epoch)
                    .await
                    .map_err(|error| if error.kind() == std::io::ErrorKind::InvalidData {
                        Error::Invalid("decoded cache repair lineage differs from the authoritative ledger")
                    } else { Error::Witness })?;
                Ok(())
            })
            .await
            .map_err(|_| Error::Witness)?
        })
    });
    if !store.set_witness(witness) {
        bail!("repair ledger publication witness is already installed");
    }
    tokio::time::timeout(OPEN_WAIT,store.reconcile()).await.context("repair ledger lineage reconciliation timed out")?
        .context("cannot confirm repair/cache lineage; check metadata and database availability, and restore matching state when a mismatch is reported")?;
    Ok(Some(store))
}
