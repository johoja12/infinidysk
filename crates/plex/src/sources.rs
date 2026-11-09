//! Explicit server-local candidate resources, bound to a verified viewer credential.
use crate::{Error, identity};
use serde::{Deserialize, Serialize};
use utoipa::ToSchema;
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash, Serialize, Deserialize, ToSchema)]
#[serde(rename_all = "snake_case")]
#[schema(as=PlexPrefetchSourceKind)]
pub enum Kind {
    #[default]
    Hub,
    Collection,
    Library,
}
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PlexPrefetchSource)]
pub struct Source {
    pub enabled: bool,
    pub server: String,
    pub viewer: String,
    pub kind: Kind,
    /// A relative Plex catalogue resource; never a supplied URL or credential.
    pub path: String,
    /// Display label retained when Plex discovery is unavailable. It grants no access.
    pub title: Option<String>,
    pub limit: u16,
    pub excluded_shows: Vec<String>,
}
impl std::fmt::Debug for Source {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Source")
            .field("server", &self.server)
            .field("viewer", &self.viewer)
            .field("kind", &self.kind)
            .field(
                "path",
                &if resource(&self.path).is_ok() {
                    self.path.as_str()
                } else {
                    "<invalid resource>"
                },
            )
            .field("limit", &self.limit)
            .finish_non_exhaustive()
    }
}
impl Default for Source {
    fn default() -> Self {
        Self {
            enabled: true,
            server: String::new(),
            viewer: String::new(),
            kind: Kind::Hub,
            path: String::new(),
            title: None,
            limit: 50,
            excluded_shows: vec![],
        }
    }
}
impl Source {
    pub fn mask_invalid_resource(&mut self, mask: &str) {
        if resource(&self.path).is_err() {
            mask.clone_into(&mut self.path);
        }
    }
    pub fn validate(&self) -> Result<(), Error> {
        if self.title.as_ref().is_some_and(|s| s.len() > 4096) {
            return Err(Error::Invalid("Plex source label is too long"));
        }
        identity(&self.server)?;
        identity(&self.viewer)?;
        if !(1..=1000).contains(&self.limit) || self.excluded_shows.len() > 1000 {
            return Err(Error::Invalid("invalid Plex source limits"));
        }
        for id in &self.excluded_shows {
            identity(id)?;
        }
        resource(&self.path)?;
        if self.path.starts_with("/hubs/") && self.kind != Kind::Hub {
            return Err(Error::Invalid(
                "Plex source kind does not match its resource",
            ));
        }
        Ok(())
    }
}
pub(crate) fn resource(path: &str) -> Result<(), Error> {
    if path.len() > 2048 || path.is_empty() {
        return Err(Error::Invalid("invalid Plex source resource"));
    }
    let mut decoded = path.to_owned();
    for _ in 0..3 {
        decoded = percent_encoding::percent_decode_str(&decoded)
            .decode_utf8()
            .map_err(|_| Error::Invalid("invalid Plex source encoding"))?
            .into_owned();
    }
    if !(decoded.starts_with("/hubs/") || decoded.starts_with("/library/"))
        || decoded.contains("..")
        || decoded.contains("//")
        || decoded.contains(['\\', '#', '%'])
        || decoded.chars().any(char::is_control)
    {
        return Err(Error::Invalid(
            "Plex source must be a relative catalogue resource",
        ));
    }
    let url = reqwest::Url::parse(&format!("http://fixture.invalid{decoded}"))
        .map_err(|_| Error::Invalid("invalid Plex source query"))?;
    if url.query_pairs().any(|(key, _)| {
        matches!(
            key.to_ascii_lowercase().as_str(),
            "x-plex-token"
                | "authorization"
                | "token"
                | "access_token"
                | "api_key"
                | "apikey"
                | "username"
                | "password"
        )
    }) {
        return Err(Error::Invalid("Plex source resource contains credentials"));
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn sources_cannot_redirect_or_smuggle_credentials_or_traversal() {
        for path in [
            "https://example.invalid/library/all",
            "//example.invalid/hubs/home",
            "/hubs/../settings",
            "/hubs/%252e%252e/settings",
            "/hubs/home?X-Plex-Token=private",
            "/library/all?%2561pi_key=private",
            "/hubs/home#fragment",
            "/hubs/home\\other",
        ] {
            assert!(resource(path).is_err(), "{path}");
        }
        assert!(resource("/hubs/home/continueWatching?includeExternalMetadata=0").is_ok());
        assert!(resource("/library/sections/1/all?type=1&sort=addedAt%3Adesc").is_ok());
    }
    #[test]
    fn invalid_resource_credentials_are_redacted_in_debug_and_settings_views() {
        let mut source = Source {
            server: "fixture".into(),
            viewer: "7".into(),
            path: "/hubs/home?token=fixture-private-value".into(),
            ..Source::default()
        };
        assert!(!format!("{source:?}").contains("fixture-private-value"));
        source.mask_invalid_resource("masked");
        assert_eq!(source.path, "masked");
    }
}
