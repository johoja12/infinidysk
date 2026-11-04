//! Typed fork policy migration. Legacy storage paths and queued operations are
//! preserved by fork-export, never activated as a side effect of reading settings.
use crate::config::{LibraryDir, Settings};
use infinidysk_import::Config;
use serde::de::DeserializeOwned;
use serde_json::{Map, Value};

fn take<T: DeserializeOwned>(
    object: &mut Map<String, Value>,
    key: &str,
    target: &mut T,
) -> Result<(), ()> {
    if let Some(value) = object.remove(key) {
        *target = serde_json::from_value(value).map_err(|_| ())?;
    }
    Ok(())
}
fn warming(raw: &str, settings: &mut Settings, notes: &mut Vec<String>) -> Result<(), ()> {
    if raw.len() > 1_048_576 {
        return Err(());
    }
    let object: Map<String, Value> = serde_json::from_str(raw).map_err(|_| ())?;
    let mut values = Map::new();
    for (key, value) in object {
        if values.insert(key.to_ascii_lowercase(), value).is_some() {
            return Err(());
        }
    }
    let mut next = settings.prefetch.clone();
    take(
        &mut values,
        "connectionsperjob",
        &mut next.connections_per_job,
    )?;
    let policy = &mut next.plex.policy;
    take(&mut values, "movieenabled", &mut policy.movies)?;
    take(&mut values, "tvenabled", &mut policy.television)?;
    take(&mut values, "lookbackdays", &mut policy.lookback_days)?;
    take(
        &mut values,
        "moviepartialwatchlookbackdays",
        &mut policy.partial_movie_lookback_days,
    )?;
    take(
        &mut values,
        "minepisodesforprediction",
        &mut policy.min_distinct_episodes,
    )?;
    take(&mut values, "fullfilewarming", &mut policy.full_file)?;
    take(&mut values, "minimumwarmenabled", &mut policy.minimum_warm)?;
    let mut head = 16u64;
    let mut tail = 8u64;
    take(&mut values, "minimumheadmb", &mut head)?;
    take(&mut values, "minimumtailmb", &mut tail)?;
    // The original writer used binary MB here. Preserve bytes while Rust displays decimal MB.
    policy.head_bytes = head.checked_mul(1_048_576).ok_or(())?;
    policy.tail_bytes = tail.checked_mul(1_048_576).ok_or(())?;
    let mut confidence = 0.5f64;
    take(&mut values, "confidencethreshold", &mut confidence)?;
    if !(0.0..=1.0).contains(&confidence) {
        return Err(());
    }
    // A fractional percent rounds upward, so migration cannot widen eligibility.
    policy.confidence_percent = (0u8..=100)
        .find(|p| f64::from(*p) >= confidence * 100.0)
        .ok_or(())?;
    take(&mut values, "historyenabled", &mut next.plex.history)?;
    take(&mut values, "realtimeenabled", &mut next.plex.realtime)?;
    take(
        &mut values,
        "tvepisodespershow",
        &mut next.plex.next_episodes,
    )?;
    take(
        &mut values,
        "finishwatchedenabled",
        &mut next.finish_watched.enabled,
    )?;
    take(
        &mut values,
        "finishwatchedpercent",
        &mut next.finish_watched.percent,
    )?;
    let mut wire = next.daily_wire_bytes;
    take(&mut values, "dailybytebudget", &mut wire)?;
    if (infinidysk_native_cache::BLOCK..=100_000_000_000_000).contains(&wire) {
        next.daily_wire_bytes = wire;
        next.daily_fill_bytes = wire;
    } else if wire == 0 {
        notes.push("Smart Prefetch unlimited legacy budget needs review; the Rust daily limits remain bounded".into());
    } else {
        return Err(());
    }
    // Transport requires a verified server:viewer identity. Legacy names and global
    // source selections cannot safely become those identities during offline import.
    for key in ["users", "sources", "disabledlibraries"] {
        if values
            .remove(key)
            .is_some_and(|v| v.as_array().is_none_or(|items| !items.is_empty()))
        {
            next.plex.policy.restrict_viewers = true;
            next.plex.policy.viewers.clear();
            notes.push("Smart Prefetch viewer/source selections need review in Rust; the explicit empty viewer selection permits no predictions. Use fork-export to preserve the original identities".into());
        }
    }
    let mut was_enabled = false;
    take(&mut values, "enabled", &mut was_enabled)?;
    next.enabled = false;
    next.plex.enabled = false;
    next.validate().map_err(|_| ())?;
    settings.prefetch = next;
    if was_enabled {
        notes.push("Smart Prefetch policy values were carried over with warming and Plex prediction refresh disabled; select new cache folders and verified viewers before enabling".into());
    }
    if !values.is_empty() {
        notes.push(format!("{} Smart Prefetch legacy fields have no direct Rust counterpart; use fork-export to preserve them without activation", values.len()));
    }
    Ok(())
}
pub(super) fn apply(cs: &Config, settings: &mut Settings, notes: &mut Vec<String>) {
    if let Some(raw) = cs.get("smart-prefetch.settings") {
        let before = settings.prefetch.clone();
        let mark = notes.len();
        if warming(raw, settings, notes).is_err() {
            settings.prefetch = before;
            notes.truncate(mark);
            notes.push("Smart Prefetch policy was not activated: invalid legacy values; use fork-export to preserve the original settings".into());
        }
    }
    if let Some(raw) = cs.get("media.library-scan-dirs") {
        let candidate = serde_json::from_str::<Vec<LibraryDir>>(raw).ok();
        if let Some(dirs) = candidate {
            let mut candidate = settings.clone();
            candidate.imports.scan_dirs = dirs;
            if candidate.validate().is_ok() {
                settings.imports.scan_dirs = candidate.imports.scan_dirs;
            } else {
                notes.push("Additional library scan folders need review: paths overlap protected storage or fail Rust validation".into());
            }
        } else {
            notes.push(
                "Additional library scan folders were not carried over: invalid legacy list".into(),
            );
        }
    }
    if let Some(raw) = cs.get("media.library-plex-server-ids") {
        match serde_json::from_str::<Vec<String>>(raw) {
            Ok(ids)
                if ids.len() <= 32
                    && ids
                        .iter()
                        .all(|id| settings.plex.servers.iter().any(|s| &s.id == id)) =>
            {
                settings.imports.plex_server_ids = Some(ids);
            }
            _ => {
                settings.imports.plex_server_ids = Some(vec![]);
                notes.push("Library Plex server selection needs review; use fork-export to preserve the original value".into());
            }
        }
    }
    if cs.get("cache.native.folders").is_some() || cs.get("cache.native.metadata-path").is_some() {
        notes.push("Native cache storage paths remain inactive; preserve them with fork-export. Select separate Rust folders, then use the verified legacy cache-copy importer".into());
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn typed_policy_preserves_bytes_without_enabling_background_work() {
        let mut settings = Settings::default();
        let mut notes = vec![];
        warming(r#"{"Enabled":true,"HistoryEnabled":true,"FinishWatchedEnabled":true,"FinishWatchedPercent":25,"MinimumHeadMb":32,"ConnectionsPerJob":3,"ConfidenceThreshold":0.501,"DailyByteBudget":20000000000,"Users":["legacy-profile"]}"#,&mut settings,&mut notes).unwrap();
        assert!(!settings.prefetch.enabled);
        assert!(!settings.prefetch.plex.enabled);
        assert!(settings.prefetch.finish_watched.enabled);
        assert_eq!(settings.prefetch.finish_watched.percent, 25);
        assert_eq!(settings.prefetch.connections_per_job, 3);
        assert_eq!(settings.prefetch.plex.policy.head_bytes, 32 * 1_048_576);
        assert_eq!(settings.prefetch.plex.policy.confidence_percent, 51);
        assert_eq!(settings.prefetch.daily_wire_bytes, 20_000_000_000);
        assert_eq!(settings.prefetch.plex.policy.viewers, Vec::<String>::new());
        assert!(settings.prefetch.plex.policy.restrict_viewers);
        assert!(notes.iter().any(|n| n.contains("viewer/source")));
    }
    #[test]
    fn malformed_or_ambiguous_case_and_unbounded_numeric_values_are_rejected() {
        for raw in [
            r#"{"Enabled":true,"enabled":false}"#,
            r#"{"ConfidenceThreshold":1.1}"#,
            r#"{"ConnectionsPerJob":9}"#,
            r#"{"MinimumHeadMb":18446744073709551615}"#,
            r#"{"FinishWatchedPercent":"25"}"#,
        ] {
            assert!(warming(raw, &mut Settings::default(), &mut vec![]).is_err());
        }
    }
    #[tokio::test]
    async fn legacy_config_import_preserves_read_only_roots_and_never_adopts_storage_paths() {
        use sqlx::ConnectOptions as _;
        let root = std::env::temp_dir().join(format!("id-fork-policy-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&root).unwrap();
        let path = root.join("db.sqlite");
        let mut db = sqlx::sqlite::SqliteConnectOptions::new()
            .filename(&path)
            .create_if_missing(true)
            .connect()
            .await
            .unwrap();
        sqlx::raw_sql("CREATE TABLE ConfigItems(ConfigName TEXT PRIMARY KEY, ConfigValue TEXT); CREATE TABLE Accounts(Username TEXT,PasswordHash TEXT,RandomSalt TEXT,Type INTEGER);").execute(&mut db).await.unwrap();
        for (key, value) in [
            (
                "smart-prefetch.settings",
                r#"{"Enabled":true,"HistoryEnabled":true,"FinishWatchedPercent":25}"#,
            ),
            ("media.library-scan-dirs", r#"["/fixture/read-only"]"#),
            ("media.library-plex-server-ids", r#"["missing-machine"]"#),
            ("cache.native.metadata-path", "/fixture/legacy-cache"),
        ] {
            sqlx::query("INSERT INTO ConfigItems VALUES($1,$2)")
                .bind(key)
                .bind(value)
                .execute(&mut db)
                .await
                .unwrap();
        }
        drop(db);
        let before = std::fs::read(&path).unwrap();
        let cs = Config::read(&root, []).await.unwrap();
        let carried =
            super::super::settings(&cs).expect("fork settings translate with OIDC validation");
        assert_eq!(
            carried.settings.imports.scan_dirs[0].path,
            "/fixture/read-only"
        );
        assert_eq!(carried.settings.imports.plex_server_ids, Some(vec![]));
        assert!(carried.settings.prefetch.plex.history);
        assert!(!carried.settings.prefetch.enabled);
        assert_eq!(
            carried.settings.native_cache.metadata_path,
            "/config/native-cache-v2"
        );
        assert_eq!(carried.settings.native_cache.folders.len(), 0);
        assert!(
            carried
                .notes
                .iter()
                .any(|note| note.contains("storage paths remain inactive"))
        );
        assert_eq!(std::fs::read(&path).unwrap(), before);
        std::fs::remove_dir_all(root).unwrap();
    }
}
