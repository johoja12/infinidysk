//! Descriptive provenance, never authority to read/delete/regrab a source.
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;
use uuid::Uuid;
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PrefetchPolicyOrigin)]
pub struct PolicyOrigin {
    #[serde(default)]
    pub viewer_name: Option<String>,
    #[serde(default)]
    pub source_title: Option<String>,
    #[serde(default)]
    pub source_path: Option<String>,
    pub source: String,
    pub server: String,
    pub viewer: String,
    pub observed_at: i64,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PrefetchReportedViewer)]
pub struct ReportedViewer {
    pub server: String,
    pub viewer: String,
    pub viewer_name: Option<String>,
    pub player: Option<String>,
    pub duration_ms: u64,
    pub observed_at: i64,
}
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PrefetchAttribution)]
pub struct Attribution {
    pub policy: Option<PolicyOrigin>,
    pub playback: Vec<ReportedViewer>,
    pub truncated: bool,
}
fn identity(value: &str) -> bool {
    !value.is_empty() && value.len() <= 256 && !value.chars().any(char::is_control)
}
impl Attribution {
    pub(super) fn valid(&self) -> bool {
        self.policy.as_ref().is_none_or(|p| {
            p.viewer_name.as_ref().is_none_or(|v| v.len() <= 1024)
                && p.source_title.as_ref().is_none_or(|v| v.len() <= 4096)
                && p.source_path.as_ref().is_none_or(|v| v.len() <= 4096)
                && identity(&p.server)
                && identity(&p.viewer)
                && p.observed_at >= 0
                && matches!(
                    p.source.as_str(),
                    "playback"
                        | "next_episode"
                        | "partial_movie"
                        | "hub"
                        | "collection"
                        | "library"
                )
        }) && self.playback.len() <= 8
            && self.playback.iter().all(|p| {
                identity(&p.server)
                    && identity(&p.viewer)
                    && p.observed_at >= 0
                    && p.viewer_name.as_ref().is_none_or(|v| v.len() <= 1024)
                    && p.player.as_ref().is_none_or(|v| v.len() <= 1024)
            })
    }
}
/// The application supplies already verified, configuration-scoped sessions. The
/// queue independently checks their source revision and observation age.
pub struct BackfillAttribution {
    pub item: Uuid,
    pub revision: String,
    pub attribution: Attribution,
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn old_policy_json_remains_readable_and_named_labels_are_bounded() {
        let mut origin: PolicyOrigin = serde_json::from_str(
            r#"{"source":"hub","server":"fixture","viewer":"1","observed_at":1}"#,
        )
        .unwrap();
        assert_eq!(origin.viewer_name, None);
        assert_eq!(origin.source_title, None);
        origin.viewer_name = Some("Fixture viewer".into());
        origin.source_title = Some("Continue watching".into());
        let mut a = Attribution {
            policy: Some(origin),
            ..Default::default()
        };
        assert!(a.valid());
        a.policy.as_mut().unwrap().source_title = Some("a".repeat(4097));
        assert!(!a.valid());
    }
}
