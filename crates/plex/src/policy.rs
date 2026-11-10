//! Policy never authorizes account access. A scheduler must resolve the viewer first.
use crate::{Error, Media, PathMapping, identity, safe_path};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use utoipa::ToSchema;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PlexPredictionPolicy)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "independent media and range policy switches"
)]
pub struct PredictionPolicy {
    pub movies: bool,
    pub television: bool,
    pub lookback_days: u32,
    pub partial_movie_lookback_days: u32,
    pub min_distinct_episodes: u32,
    pub confidence_percent: u8,
    /// Exact `server:viewer` identities; names do not authorize or select profiles.
    pub viewers: Vec<String>,
    /// An explicitly empty selection permits no viewers.
    pub restrict_viewers: bool,
    pub excluded_shows: Vec<String>,
    pub full_file: bool,
    /// Add head/tail retention to partial warming windows.
    pub minimum_warm: bool,
    pub head_bytes: u64,
    pub tail_bytes: u64,
}
impl Default for PredictionPolicy {
    fn default() -> Self {
        Self {
            movies: true,
            television: true,
            lookback_days: 7,
            partial_movie_lookback_days: 7,
            min_distinct_episodes: 2,
            confidence_percent: 50,
            viewers: vec![],
            restrict_viewers: false,
            excluded_shows: vec![],
            full_file: true,
            minimum_warm: false,
            head_bytes: 16 * 1024 * 1024,
            tail_bytes: 8 * 1024 * 1024,
        }
    }
}
impl PredictionPolicy {
    pub fn validate(&self) -> Result<(), Error> {
        if self.lookback_days == 0
            || self.lookback_days > 365
            || self.partial_movie_lookback_days > 365
            || !(1..=100).contains(&self.min_distinct_episodes)
            || self.confidence_percent > 100
            || self.viewers.len() > 1000
            || self.viewers.iter().any(|value| {
                value
                    .split_once(':')
                    .is_none_or(|(s, v)| identity(s).is_err() || identity(v).is_err())
            })
            || self
                .excluded_shows
                .iter()
                .any(|value| identity(value).is_err())
            || self.excluded_shows.len() > 10_000
            || self.head_bytes > 1024 * 1024 * 1024
            || self.tail_bytes > 1024 * 1024 * 1024
        {
            return Err(Error::Invalid("invalid Plex prediction bounds"));
        }
        Ok(())
    }
    pub fn allows_viewer(&self, server: &str, viewer: &str) -> bool {
        (!self.restrict_viewers && self.viewers.is_empty())
            || self.viewers.contains(&format!("{server}:{viewer}"))
    }
    pub fn eligible(&self, item: &Media) -> bool {
        match item.kind.as_str() {
            "movie" => self.movies,
            "episode" | "show" => {
                self.television
                    && !self
                        .excluded_shows
                        .iter()
                        .any(|s| s == item.show.as_ref().unwrap_or(&item.rating_key))
            }
            _ => false,
        }
    }
    /// A bounded, newest-first snapshot. Profiles and shows group independently.
    pub fn history_candidates(
        &self,
        items: &[Media],
        server: &str,
        now_seconds: i64,
    ) -> Vec<Media> {
        let threshold = now_seconds.saturating_sub(i64::from(self.lookback_days) * 86400);
        let movie_threshold =
            now_seconds.saturating_sub(i64::from(self.partial_movie_lookback_days) * 86400);
        let mut selected: Vec<_> = items
            .iter()
            .take(1000)
            .filter(|item| {
                self.eligible(item)
                    && item
                        .viewed_at
                        .is_some_and(|at| at >= threshold && at <= now_seconds)
                    && item
                        .user
                        .as_ref()
                        .is_some_and(|user| self.allows_viewer(server, user))
            })
            .collect();
        selected.sort_by_key(|item| std::cmp::Reverse(item.viewed_at));
        let mut result = Vec::new();
        let mut movies = HashSet::new();
        let mut shows: BTreeMap<_, Vec<&Media>> = BTreeMap::new();
        for item in selected {
            if item.kind == "movie"
                && item.view_offset_ms > 0
                && item.duration_ms > item.view_offset_ms
                && item.viewed_at.is_some_and(|at| at >= movie_threshold)
                && movies.insert((&item.user, &item.rating_key))
            {
                result.push(item.clone());
            }
            if item.kind == "episode"
                && let (Some(user), Some(show)) = (&item.user, &item.show)
            {
                shows.entry((user, show)).or_default().push(item);
            }
        }
        for group in shows.into_values() {
            let count = group
                .iter()
                .map(|item| &item.rating_key)
                .collect::<HashSet<_>>()
                .len();
            if count >= self.min_distinct_episodes as usize
                && (count as u64) * 100
                    >= u64::from(self.confidence_percent)
                        * 2
                        * u64::from(self.min_distinct_episodes)
            {
                result.push(group[0].clone());
            }
        }
        result.sort_by_key(|item| std::cmp::Reverse(item.viewed_at));
        result.truncate(128);
        result
    }
    /// Merge playback-window and minimum head/tail policies before admission.
    pub fn warming_ranges(&self, length: u64, offset_ms: u64, duration_ms: u64) -> Vec<(u64, u64)> {
        let mut ranges = self.ranges(length, offset_ms, duration_ms, false);
        if self.minimum_warm && !self.full_file {
            ranges.extend(self.ranges(length, 0, 0, true));
        }
        ranges.sort_unstable();
        let mut merged: Vec<(u64, u64)> = Vec::new();
        for (start, len) in ranges {
            let end = start.saturating_add(len).min(length);
            if let Some(last) = merged.last_mut().filter(|last| start <= last.0 + last.1) {
                last.1 = (last.0 + last.1).max(end) - last.0;
            } else if start < end {
                merged.push((start, end - start));
            }
        }
        merged
    }
    /// Ranges are block-aligned for final-byte retention. Time-to-byte is a hint only.
    pub fn ranges(
        &self,
        length: u64,
        offset_ms: u64,
        duration_ms: u64,
        minimum: bool,
    ) -> Vec<(u64, u64)> {
        const BLOCK: u64 = 4 * 1024 * 1024;
        if length == 0 {
            return vec![];
        }
        if minimum {
            let head = self
                .head_bytes
                .min(length)
                .div_ceil(BLOCK)
                .saturating_mul(BLOCK)
                .min(length);
            let tail = self.tail_bytes.min(length);
            let start = (length - tail) / BLOCK * BLOCK;
            if tail > 0 && start <= head {
                return vec![(0, length)];
            }
            let mut result = Vec::new();
            if head > 0 {
                result.push((0, head));
            }
            if tail > 0 {
                result.push((start, length - start));
            }
            return result;
        }
        if self.full_file {
            return vec![(0, length)];
        }
        let offset = if duration_ms == 0 {
            0
        } else {
            u64::try_from(
                u128::from(offset_ms.min(duration_ms)) * u128::from(length)
                    / u128::from(duration_ms),
            )
            .unwrap_or(length)
        };
        let start = offset.saturating_sub(BLOCK) / BLOCK * BLOCK;
        vec![(start, (64 * 1024 * 1024).min(length - start))]
    }
}
pub fn next_episodes(current: &Media, items: &[Media], limit: usize) -> Vec<Media> {
    let show = current.show.as_ref().unwrap_or(&current.rating_key);
    let mut items: Vec<_> = items
        .iter()
        .filter(|item| {
            item.kind == "episode"
                && item.view_count == 0
                && item.show.as_ref() == Some(show)
                && item.rating_key != current.rating_key
                && item.season.is_some_and(|s| s > 0)
                && item.episode.is_some_and(|e| e > 0)
                && (current.kind == "show"
                    || (item.season, item.episode) > (current.season, current.episode))
        })
        .collect();
    items.sort_by_key(|item| (item.season, item.episode));
    let mut seen = HashSet::new();
    items
        .into_iter()
        .filter(|item| seen.insert(&item.rating_key))
        .take(limit.clamp(1, 20))
        .cloned()
        .collect()
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum MappedPath {
    Dav(String),
    Local(String),
}
pub fn map_path(file: &str, mappings: &[PathMapping]) -> Option<MappedPath> {
    if !safe_path(file) {
        return None;
    }
    let normalized = file.replace('\\', "/");
    let windows = normalized.as_bytes().get(1) == Some(&b':');
    let comparison = if windows {
        normalized.to_ascii_lowercase()
    } else {
        normalized.clone()
    };
    let mut matches: Vec<_> = mappings
        .iter()
        .filter_map(|mapping| {
            let prefix = mapping
                .plex_path
                .replace('\\', "/")
                .trim_end_matches('/')
                .to_owned();
            let match_prefix = if windows {
                prefix.to_ascii_lowercase()
            } else {
                prefix.clone()
            };
            comparison
                .starts_with(&format!("{match_prefix}/"))
                .then_some((mapping, prefix))
        })
        .collect();
    matches.sort_by_key(|(_, prefix)| std::cmp::Reverse(prefix.len()));
    let (mapping, prefix) = matches.first()?;
    if matches
        .iter()
        .skip(1)
        .any(|(other, p)| p.len() == prefix.len() && other != mapping)
    {
        return None;
    }
    let suffix = &normalized[prefix.len()..];
    match (&mapping.dav_path, &mapping.local_path) {
        (Some(path), None) if safe_path(path) => Some(MappedPath::Dav(format!(
            "{}{suffix}",
            path.trim_end_matches('/')
        ))),
        (None, Some(path)) if safe_path(path) => Some(MappedPath::Local(format!(
            "{}{suffix}",
            path.trim_end_matches(['/', '\\'])
        ))),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn item(viewer: &str, key: &str, episode: u32) -> Media {
        Media {
            artwork: false,
            year: None,
            resolution: None,
            rating_key: key.into(),
            kind: "episode".into(),
            title: "fixture".into(),
            show: Some("show".into()),
            show_title: None,
            season: Some(1),
            episode: Some(episode),
            file: None,
            view_count: 0,
            view_offset_ms: 1,
            duration_ms: 100,
            viewed_at: Some(1000),
            user: Some(viewer.into()),
        }
    }
    #[test]
    fn distinct_episode_evidence_never_leaks_between_viewers() {
        let policy = PredictionPolicy::default();
        let a = item("a", "one", 1);
        let b = item("b", "two", 2);
        assert_eq!(
            policy.history_candidates(&[a.clone(), a.clone(), b], "server", 1000),
            []
        );
        let result = policy.history_candidates(
            &[a, item("a", "two", 2), item("b", "three", 3)],
            "server",
            1000,
        );
        assert_eq!(result.len(), 1);
        assert_eq!(result[0].user.as_deref(), Some("a"));
        let restricted = PredictionPolicy {
            viewers: vec!["server:b".into()],
            ..policy
        };
        assert_eq!(restricted.history_candidates(&result, "server", 1000), []);
    }
    #[test]
    fn next_episodes_exclude_specials_other_shows_and_duplicates() {
        let current = item("a", "one", 1);
        let next = item("a", "two", 2);
        let mut special = item("a", "special", 3);
        special.season = Some(0);
        let mut other = item("a", "other", 3);
        other.show = Some("another".into());
        assert_eq!(
            next_episodes(&current, &[next.clone(), next.clone(), special, other], 2),
            [next]
        );
    }
    #[test]
    fn exact_longest_mapping_wins_and_ambiguous_or_traversing_paths_fail() {
        let mapping = |source: &str, target: &str| PathMapping {
            plex_path: source.into(),
            dav_path: Some(target.into()),
            local_path: None,
        };
        let maps = [
            mapping("/library", "/content"),
            mapping("/library/specific", "/content/specific"),
        ];
        assert_eq!(
            map_path("/library/specific/fixture.dat", &maps),
            Some(MappedPath::Dav("/content/specific/fixture.dat".into()))
        );
        assert_eq!(map_path("/library-other/fixture.dat", &maps), None);
        assert_eq!(map_path("/library/../fixture.dat", &maps), None);
        assert_eq!(
            map_path(
                "/library/fixture.dat",
                &[mapping("/library", "/a"), mapping("/library", "/b")]
            ),
            None
        );
        assert_eq!(
            map_path(
                "c:\\Library\\fixture.dat",
                &[mapping("C:/library", "/content")]
            ),
            Some(MappedPath::Dav("/content/fixture.dat".into()))
        );
    }
    #[test]
    fn minimum_and_playback_ranges_do_not_escape_the_file() {
        let policy = PredictionPolicy::default();
        assert_eq!(policy.ranges(100, 0, 0, true), [(0, 100)]);
        let ranges = policy.ranges(100_000_000, 0, 0, true);
        assert_eq!(ranges.len(), 2);
        assert_eq!(ranges[1].0 + ranges[1].1, 100_000_000);
        let partial = PredictionPolicy {
            full_file: false,
            ..policy
        };
        for length in [1, 100, 100_000_000, u64::MAX] {
            for (start, len) in partial.ranges(length, u64::MAX, u64::MAX, false) {
                assert!(start <= length && len <= length - start);
            }
        }
    }
    #[test]
    fn viewer_filters_require_exact_ids_instead_of_display_names() {
        let mut policy = PredictionPolicy {
            viewers: vec!["display name".into()],
            ..Default::default()
        };
        assert!(policy.validate().is_err());
        policy.viewers = vec!["machine:7".into()];
        assert!(policy.validate().is_ok());
    }
    #[test]
    fn minimum_warming_preserves_playback_window_and_merges_overlap() {
        let mut policy = PredictionPolicy {
            full_file: false,
            minimum_warm: true,
            head_bytes: 16_000_000,
            tail_bytes: 8_000_000,
            ..PredictionPolicy::default()
        };
        let ranges = policy.warming_ranges(1_000_000_000, 5000, 10000);
        assert_eq!(ranges.len(), 3);
        assert_eq!(ranges[0].0, 0);
        assert!(ranges[1].0 < 500_000_000);
        assert_eq!(ranges[2].0 + ranges[2].1, 1_000_000_000);
        assert_eq!(policy.warming_ranges(5_000_000, 0, 1000), [(0, 5_000_000)]);
        policy.full_file = true;
        assert_eq!(
            policy.warming_ranges(1_000_000_000, 5000, 10000),
            [(0, 1_000_000_000)]
        );
    }
    #[test]
    fn explicit_empty_viewer_selection_never_broadens_to_all_viewers() {
        let mut policy = PredictionPolicy::default();
        assert!(policy.allows_viewer("machine", "7"));
        policy.restrict_viewers = true;
        assert!(!policy.allows_viewer("machine", "7"));
        policy.viewers.push("machine:7".into());
        assert!(policy.allows_viewer("machine", "7"));
        assert!(!policy.allows_viewer("other-machine", "7"));
    }
}
