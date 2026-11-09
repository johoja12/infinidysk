//! Additional scan roots permit discovery, never destructive remediation.
use crate::config::Settings;
use infinidysk_core::ItemId;
use infinidysk_db::{Db, Error};
use infinidysk_health::RepairTarget;
use std::path::{Component, Path};
fn within(path: &str, root: &str) -> bool {
    Path::new(path).is_absolute()
        && !path.chars().any(char::is_control)
        && !Path::new(path)
            .components()
            .any(|c| matches!(c, Component::ParentDir))
        && Path::new(path).starts_with(root)
}
pub(crate) async fn allows(
    db: &Db,
    settings: &Settings,
    item: ItemId,
    target: RepairTarget,
) -> Result<bool, Error> {
    let imports = &settings.imports;
    if imports.scan_dirs.is_empty() {
        return Ok(true);
    }
    match target {
        RepairTarget::UnownedDelete => return Ok(false),
        RepairTarget::ArrPath(path) => {
            return Ok(!imports
                .scan_dirs
                .iter()
                .any(|root| within(&path, root.arr_path()))
                && imports
                    .library_dirs
                    .iter()
                    .any(|root| within(&path, root.arr_path())));
        }
        RepairTarget::Inspect => {}
    }
    let links = db
        .catalogue_links(None, None, Some(item.0), false, "", 1000)
        .await?;
    let primary = links.iter().any(|link| {
        imports
            .library_dirs
            .iter()
            .any(|root| root.path == link.root)
    });
    // Stale discovery can conservatively hold a repair, but cannot create authority
    // over a scan-only path. Exact current Arr root/file checks remain mandatory.
    Ok(primary)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::LibraryDir;
    use infinidysk_core::ItemKind;
    use infinidysk_db::{Item, catalogue::Link};
    #[tokio::test]
    async fn scan_links_and_arr_paths_cannot_authorize_replacement() {
        let db = Db::connect(&format!(
            "sqlite:{}",
            std::env::temp_dir()
                .join(format!("rust-scan-authority-{}.sqlite", ItemId::new()))
                .display()
        ))
        .await
        .expect("db");
        let content = db
            .item(ItemId::CONTENT)
            .await
            .expect("query")
            .expect("root");
        let item = Item::child(&content, "fixture.mkv", ItemKind::NzbFile).expect("file");
        db.insert(std::slice::from_ref(&item), &[])
            .await
            .expect("insert");
        let mut settings = Settings::default();
        assert!(
            allows(&db, &settings, item.id, RepairTarget::Inspect)
                .await
                .expect("default")
        );
        settings.imports.scan_dirs.push(LibraryDir {
            path: "/scan".into(),
            arr_path: "/arr/scan".into(),
        });
        settings.imports.library_dirs.push(LibraryDir {
            path: "/primary".into(),
            arr_path: "/arr/primary".into(),
        });
        assert!(
            !allows(
                &db,
                &settings,
                item.id,
                RepairTarget::ArrPath("/arr/scan/fixture.mkv".into())
            )
            .await
            .expect("mapped readonly")
        );
        assert!(!within("/arr/scanner/fixture.mkv", "/arr/scan"));
        assert!(
            allows(
                &db,
                &settings,
                item.id,
                RepairTarget::ArrPath("/arr/primary/fixture.mkv".into())
            )
            .await
            .expect("managed Arr path")
        );
        assert!(
            !allows(&db, &settings, item.id, RepairTarget::Inspect)
                .await
                .expect("unknown source has no writable authority")
        );
        let mut link = Link {
            root: "/scan".into(),
            path: "/scan/fixture.mkv".into(),
            relative_path: "fixture.mkv".into(),
            group_name: "fixture".into(),
            item_id: item.id.0,
            pointer_hash: "opaque".into(),
            link_kind: 1,
            video: 1,
        };
        db.catalogue_replace("/scan", "scope", std::slice::from_ref(&link), 0, 0)
            .await
            .expect("index");
        assert!(
            !allows(&db, &settings, item.id, RepairTarget::Inspect)
                .await
                .expect("scan-only index")
        );
        link.root = "/primary".into();
        link.path = "/primary/fixture.mkv".into();
        db.catalogue_replace("/primary", "scope", &[link], 0, 0)
            .await
            .expect("primary index");
        assert!(
            allows(&db, &settings, item.id, RepairTarget::Inspect)
                .await
                .expect("primary mapping")
        );
        assert!(
            !allows(
                &db,
                &settings,
                item.id,
                RepairTarget::ArrPath("/arr/scan/fixture.mkv".into())
            )
            .await
            .expect("readonly owner still denied")
        );
        assert!(
            !allows(&db, &settings, item.id, RepairTarget::UnownedDelete)
                .await
                .expect("index cannot authorize unowned deletion")
        );
        assert!(
            !allows(
                &db,
                &settings,
                item.id,
                RepairTarget::ArrPath("/outside/fixture.mkv".into())
            )
            .await
            .expect("index cannot authorize outside Arr paths")
        );
    }
}
