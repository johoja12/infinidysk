//! Fork Plex transport and policy. Tokens never appear in URLs, diagnostics or Debug.
mod policy;
pub mod sources;
mod transport;
mod xml;

use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fmt;
use utoipa::ToSchema;

pub use policy::*;
pub use transport::{Client, Error, Pin};

#[derive(Clone, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(transparent)]
#[schema(as=PlexToken)]
pub struct Token(pub String);
impl fmt::Debug for Token {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("[redacted]")
    }
}
#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[serde(default)]
#[schema(as=PlexSettings)]
pub struct Settings {
    pub accounts: Vec<Account>,
    pub servers: Vec<Server>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexAccount)]
pub struct Account {
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Name")]
    pub name: String,
    #[serde(alias = "Token")]
    pub token: Token,
    #[serde(default)]
    pub numeric_id: Option<String>,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexServer)]
pub struct Server {
    #[serde(alias = "Id")]
    pub id: String,
    #[serde(alias = "Name")]
    pub name: String,
    #[serde(alias = "Url")]
    pub url: String,
    #[serde(alias = "Token")]
    pub token: Token,
    #[serde(alias = "Enabled", default = "yes")]
    pub enabled: bool,
    #[serde(alias = "AccountId", default)]
    pub account_id: Option<String>,
    #[serde(alias = "PathMappings", default)]
    pub path_mappings: Vec<PathMapping>,
}
const fn yes() -> bool {
    true
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexPathMapping)]
pub struct PathMapping {
    #[serde(alias = "PlexPath")]
    pub plex_path: String,
    #[serde(alias = "DavPath")]
    pub dav_path: Option<String>,
    #[serde(alias = "LocalPath", default)]
    pub local_path: Option<String>,
}
impl Settings {
    pub fn validate(&self) -> Result<(), Error> {
        if self.accounts.len() > 16 || self.servers.len() > 32 {
            return Err(Error::Invalid("too many Plex accounts or servers"));
        }
        let mut accounts = HashSet::new();
        let mut servers = HashSet::new();
        for account in &self.accounts {
            identity(&account.id)?;
            secret(&account.token)?;
            if account.name.trim().is_empty()
                || account.name.encode_utf16().count() > 256
                || !accounts.insert(&account.id)
            {
                return Err(Error::Invalid("invalid or duplicate Plex account"));
            }
        }
        for server in &self.servers {
            identity(&server.id)?;
            secret(&server.token)?;
            transport::server_url(&server.url)?;
            if !servers.insert(&server.id)
                || server.name.encode_utf16().count() > 256
                || server.path_mappings.len() > 64
            {
                return Err(Error::Invalid("invalid or duplicate Plex server"));
            }
            if let Some(account) = &server.account_id {
                identity(account)?;
            }
            for mapping in &server.path_mappings {
                if !safe_path(&mapping.plex_path)
                    || (mapping.dav_path.is_some() == mapping.local_path.is_some())
                    || mapping
                        .dav_path
                        .as_ref()
                        .is_some_and(|p| !p.starts_with('/') || !safe_path(p))
                    || mapping.local_path.as_ref().is_some_and(|p| !safe_path(p))
                {
                    return Err(Error::Invalid(
                        "Plex mappings require one absolute target and a safe source path",
                    ));
                }
            }
        }
        Ok(())
    }
    #[must_use]
    pub fn masked(&self, mask: &str) -> Self {
        let mut out = self.clone();
        for account in &mut out.accounts {
            if !account.token.0.is_empty() {
                mask.clone_into(&mut account.token.0);
            }
        }
        for server in &mut out.servers {
            if !server.token.0.is_empty() {
                mask.clone_into(&mut server.token.0);
            }
        }
        out
    }
    pub fn restore(&mut self, current: &Self, mask: &str) {
        for account in &mut self.accounts {
            if account.token.0 == mask
                && let Some(old) = current.accounts.iter().find(|a| a.id == account.id)
            {
                account.token.clone_from(&old.token);
            }
        }
        for server in &mut self.servers {
            if server.token.0 == mask
                && let Some(old) = current.servers.iter().find(|s| s.id == server.id)
            {
                server.token.clone_from(&old.token);
            }
        }
    }
}
fn identity(id: &str) -> Result<(), Error> {
    if id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        Err(Error::Invalid("invalid Plex identity"))
    } else {
        Ok(())
    }
}
fn secret(token: &Token) -> Result<(), Error> {
    if token.0.is_empty()
        || token.0 == "********"
        || token.0.len() > 8192
        || token.0.chars().any(char::is_control)
    {
        Err(Error::Invalid("invalid Plex credential"))
    } else {
        Ok(())
    }
}
fn safe_path(path: &str) -> bool {
    let absolute = path.starts_with('/')
        || (path.as_bytes().first().is_some_and(u8::is_ascii_alphabetic)
            && path.as_bytes().get(1) == Some(&b':')
            && path
                .as_bytes()
                .get(2)
                .is_some_and(|b| matches!(b, b'/' | b'\\')));
    absolute
        && path.len() <= 4096
        && !path.chars().any(char::is_control)
        && !path
            .split(['/', '\\'])
            .any(|part| matches!(part, "." | ".."))
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexConnection)]
pub struct Connection {
    pub uri: String,
    pub local: bool,
    pub relay: bool,
}
#[derive(Clone, Debug)]
pub struct Resource {
    pub id: String,
    pub name: String,
    pub token: Token,
    pub connections: Vec<Connection>,
    pub owned: bool,
}
#[derive(Clone, Debug, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexHomeUser)]
pub struct HomeUser {
    pub id: String,
    pub name: String,
    pub protected: bool,
    pub admin: bool,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexLibrary)]
pub struct Library {
    pub id: String,
    pub title: String,
    pub kind: String,
}
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexMedia)]
pub struct Media {
    pub rating_key: String,
    pub kind: String,
    pub title: String,
    pub show: Option<String>,
    pub show_title: Option<String>,
    pub season: Option<u32>,
    pub episode: Option<u32>,
    pub file: Option<String>,
    #[serde(default)]
    pub view_count: u32,
    pub view_offset_ms: u64,
    pub duration_ms: u64,
    pub viewed_at: Option<i64>,
    pub user: Option<String>,
}
/// Every explicit Part path for a metadata identity, including multiple versions.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CatalogueMedia {
    pub media: Media,
    pub files: Vec<String>,
    pub year: Option<u32>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, ToSchema)]
#[schema(as=PlexSession)]
pub struct Session {
    pub id: String,
    pub user: String,
    pub user_name: Option<String>,
    pub player: Option<String>,
    pub state: String,
    pub media: Media,
}

/// Only the transport resolver can construct a viewer-scoped credential. A configured
/// display name or claimed account id alone cannot authorize watched-state queries.
#[derive(Clone, Debug)]
pub struct ViewerServer {
    pub(crate) server: Server,
    pub(crate) viewer: String,
}
impl ViewerServer {
    pub fn server_id(&self) -> &str {
        &self.server.id
    }
    pub fn viewer_id(&self) -> &str {
        &self.viewer
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn settings() -> Settings {
        Settings {
            accounts: vec![Account {
                id: "viewer".into(),
                name: "fixture".into(),
                token: Token("fixture-account-token".into()),
                numeric_id: None,
            }],
            servers: vec![Server {
                id: "machine".into(),
                name: "fixture".into(),
                url: "https://plex.invalid".into(),
                token: Token("fixture-server-token".into()),
                enabled: true,
                account_id: Some("viewer".into()),
                path_mappings: vec![],
            }],
        }
    }
    #[test]
    fn masked_settings_round_trip_without_exposing_tokens() {
        let current = settings();
        let mut masked = current.masked("********");
        assert!(
            !serde_json::to_string(&masked)
                .expect("JSON")
                .contains("fixture-account-token")
        );
        assert!(!format!("{current:?}").contains("fixture-account-token"));
        masked.restore(&current, "********");
        assert_eq!(masked, current);
        masked.accounts[0].id = "different".into();
        masked.accounts[0].token = Token("********".into());
        masked.restore(&current, "********");
        assert_eq!(masked.accounts[0].token.0, "********");
        assert!(masked.validate().is_err());
    }
    #[test]
    fn server_urls_cannot_hide_credentials_or_query_tokens() {
        for url in [
            "https://user:password@plex.invalid",
            "https://plex.invalid?X-Plex-Token=fixture",
            "https://plex.invalid/#fragment",
            "file:///fixture",
            "http://p\nlex.invalid",
        ] {
            let mut invalid = settings();
            invalid.servers[0].url = url.into();
            assert!(invalid.validate().is_err(), "{url}");
        }
        settings().validate().expect("safe settings");
    }
    #[test]
    fn legacy_account_and_server_json_are_accepted_for_explicit_import() {
        let account: Account = serde_json::from_str(
            r#"{"Id":"viewer","Name":"fixture","Token":"fixture-account-token"}"#,
        )
        .expect("1.x account");
        let server:Server=serde_json::from_str(r#"{"Id":"machine","Name":"fixture","Url":"https://plex.invalid","Token":"fixture-server-token","Enabled":true,"AccountId":"viewer","PathMappings":[{"PlexPath":"/library","DavPath":"/content","LocalPath":null}]}"#).expect("1.x server");
        Settings {
            accounts: vec![account],
            servers: vec![server],
        }
        .validate()
        .expect("valid imported configuration");
    }
}
