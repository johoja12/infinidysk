use crate::{
    Account, CatalogueMedia, Connection, HomeUser, Library, Media, Resource, Server, Session,
    Token, ViewerServer, identity, secret, xml,
};
use reqwest::header::{ACCEPT, HeaderValue};
use reqwest::{Method, Url};
use serde_json::Value;
use std::collections::HashSet;
use std::fmt;
use std::time::Duration;
use uuid::Uuid;

const LIMIT: usize = 4 * 1024 * 1024;
#[derive(Clone, Copy, Debug, thiserror::Error)]
pub enum Error {
    #[error("{0}")]
    Invalid(&'static str),
    #[error("Plex no longer exposes this resource")]
    NotFound,
    #[error("Plex rejected the credential")]
    Unauthorized,
    #[error("Plex is limiting requests")]
    RateLimited,
    #[error("Plex could not be reached")]
    Unavailable,
    #[error("Plex request timed out")]
    Timeout,
}
#[derive(Clone)]
pub struct Client {
    http: reqwest::Client,
    cloud: Url,
    installation: String,
}
pub struct Pin {
    pub id: u64,
    pub code: String,
    pub expires_in: u64,
    pub token: Option<Token>,
}
impl fmt::Debug for Pin {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("Pin { credentials = [redacted] }")
    }
}
impl Client {
    pub fn new(installation: Uuid) -> Result<Self, Error> {
        Ok(Self {
            http: reqwest::Client::builder()
                .timeout(Duration::from_secs(15))
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .map_err(|_| Error::Unavailable)?,
            cloud: Url::parse("https://plex.tv/").expect("literal URL"),
            installation: installation.to_string(),
        })
    }
    /// Local fixture constructor; never accepts a remote credential endpoint.
    #[cfg(feature = "test-support")]
    pub fn loopback(installation: Uuid, port: u16) -> Result<Self, Error> {
        let mut client = Self::new(installation)?;
        client.cloud = Url::parse(&format!("http://127.0.0.1:{port}/"))
            .map_err(|_| Error::Invalid("invalid fixture endpoint"))?;
        Ok(client)
    }
    pub fn installation(&self) -> &str {
        &self.installation
    }
    async fn send(
        &self,
        method: Method,
        url: Url,
        token: Option<&Token>,
        form: Option<Vec<(&str, String)>>,
        accept: &'static str,
    ) -> Result<Vec<u8>, Error> {
        let mut request = self
            .http
            .request(method, url)
            .header("X-Plex-Client-Identifier", &self.installation)
            .header("X-Plex-Product", "InfiniDysk")
            .header("X-Plex-Version", "2")
            .header(ACCEPT, accept);
        if let Some(token) = token {
            secret(token)?;
            let mut value = HeaderValue::from_str(&token.0)
                .map_err(|_| Error::Invalid("invalid Plex credential"))?;
            value.set_sensitive(true);
            request = request.header("X-Plex-Token", value);
        }
        if let Some(form) = form {
            request = request
                .header("Content-Type", "application/x-www-form-urlencoded")
                .body(
                    form.into_iter()
                        .map(|(key, value)| format!("{}={}", encode(key), encode(&value)))
                        .collect::<Vec<_>>()
                        .join("&"),
                );
        }
        let transfer = async {
            let mut response = request.send().await.map_err(|_| Error::Unavailable)?;
            match response.status().as_u16() {
                401 | 403 => return Err(Error::Unauthorized),
                404 => return Err(Error::NotFound),
                429 => return Err(Error::RateLimited),
                200..=299 => {}
                _ => return Err(Error::Unavailable),
            }
            if response
                .content_length()
                .is_some_and(|len| len > LIMIT as u64)
            {
                return Err(Error::Invalid("Plex response exceeds size limit"));
            }
            let mut bytes = Vec::new();
            while let Some(chunk) = response.chunk().await.map_err(|_| Error::Unavailable)? {
                if chunk.len() > LIMIT.saturating_sub(bytes.len()) {
                    return Err(Error::Invalid("Plex response exceeds size limit"));
                }
                bytes.extend_from_slice(&chunk);
            }
            Ok(bytes)
        };
        tokio::time::timeout(Duration::from_secs(15), transfer)
            .await
            .map_err(|_| Error::Timeout)?
    }
    async fn json(
        &self,
        method: Method,
        path: &str,
        token: Option<&Token>,
        form: Option<Vec<(&str, String)>>,
    ) -> Result<Value, Error> {
        let url = self
            .cloud
            .join(path)
            .map_err(|_| Error::Invalid("invalid Plex resource"))?;
        serde_json::from_slice(
            &self
                .send(method, url, token, form, "application/json")
                .await?,
        )
        .map_err(|_| Error::Invalid("invalid Plex JSON"))
    }
    pub async fn start_pin(&self) -> Result<Pin, Error> {
        pin(&self
            .json(
                Method::POST,
                "api/v2/pins",
                None,
                Some(vec![("strong", "true".into())]),
            )
            .await?)
    }
    pub async fn poll_pin(&self, id: u64) -> Result<Pin, Error> {
        pin(&self
            .json(Method::GET, &format!("api/v2/pins/{id}"), None, None)
            .await?)
    }
    pub async fn account(&self, token: &Token) -> Result<Account, Error> {
        let value = self
            .json(Method::GET, "api/v2/user", Some(token), None)
            .await?;
        let id = text(&value, "uuid")
            .or_else(|| scalar(&value, "id"))
            .ok_or(Error::Invalid("Plex account identity is missing"))?;
        identity(&id)?;
        let name = text(&value, "username")
            .or_else(|| text(&value, "title"))
            .ok_or(Error::Invalid("Plex account name is missing"))?;
        Ok(Account {
            id,
            name,
            token: token.clone(),
            numeric_id: scalar(&value, "id"),
        })
    }
    pub async fn home_users(&self, token: &Token) -> Result<Vec<HomeUser>, Error> {
        let value = self
            .json(Method::GET, "api/v2/home/users", Some(token), None)
            .await?;
        let items = value
            .as_array()
            .or_else(|| value.get("users").and_then(Value::as_array))
            .ok_or(Error::Invalid("invalid Plex Home users"))?;
        if items.len() > 100 {
            return Err(Error::Invalid("too many Plex Home users"));
        }
        items
            .iter()
            .map(|value| {
                let id = text(value, "uuid")
                    .or_else(|| scalar(value, "id"))
                    .ok_or(Error::Invalid("Plex Home user identity missing"))?;
                identity(&id)?;
                Ok(HomeUser {
                    id,
                    name: text(value, "title")
                        .or_else(|| text(value, "username"))
                        .unwrap_or_default(),
                    protected: boolean(value, "protected"),
                    admin: boolean(value, "admin"),
                })
            })
            .collect()
    }
    pub async fn switch_home(
        &self,
        token: &Token,
        user: &str,
        pin: Option<&str>,
    ) -> Result<Account, Error> {
        identity(user)?;
        if pin.is_some_and(|pin| pin.len() != 4 || !pin.bytes().all(|b| b.is_ascii_digit())) {
            return Err(Error::Invalid("Plex Home PIN must contain four digits"));
        }
        let form = pin.map_or_else(Vec::new, |pin| vec![("pin", pin.to_owned())]);
        let value = self
            .json(
                Method::POST,
                &format!("api/v2/home/users/{user}/switch"),
                Some(token),
                Some(form),
            )
            .await?;
        let token = Token(
            text(&value, "authToken")
                .ok_or(Error::Invalid("Plex Home did not authorize the user"))?,
        );
        let account = self.account(&token).await?;
        if account.id != user && account.numeric_id.as_deref() != Some(user) {
            return Err(Error::Invalid(
                "Plex Home returned a different account identity",
            ));
        }
        Ok(account)
    }
    pub async fn discover(&self, token: &Token) -> Result<Vec<Resource>, Error> {
        let value = self
            .json(
                Method::GET,
                "api/v2/resources?includeHttps=1",
                Some(token),
                None,
            )
            .await?;
        let items = value
            .as_array()
            .ok_or(Error::Invalid("invalid Plex resource list"))?;
        if items.len() > 128 {
            return Err(Error::Invalid("too many Plex resources"));
        }
        let mut result = Vec::new();
        for value in items {
            let server = text(value, "provides")
                .is_some_and(|p| p.split(',').any(|p| p == "server"))
                || value
                    .get("roles")
                    .and_then(Value::as_array)
                    .is_some_and(|roles| roles.iter().any(|v| v.as_str() == Some("server")));
            if !server {
                continue;
            }
            let id = text(value, "clientIdentifier")
                .ok_or(Error::Invalid("Plex server identity missing"))?;
            identity(&id)?;
            let mut connections = Vec::new();
            if let Some(items) = value.get("connections").and_then(Value::as_array) {
                if items.len() > 32 {
                    return Err(Error::Invalid("too many Plex server endpoints"));
                }
                for item in items {
                    if let Some(uri) = text(item, "uri")
                        && server_url(&uri).is_ok()
                    {
                        connections.push(Connection {
                            uri,
                            local: boolean(item, "local"),
                            relay: boolean(item, "relay"),
                        });
                    }
                }
            }
            result.push(Resource {
                id,
                name: text(value, "name").unwrap_or_default(),
                token: Token(text(value, "accessToken").unwrap_or_else(|| token.0.clone())),
                connections,
                owned: boolean(value, "owned"),
            });
        }
        Ok(result)
    }
    async fn xml(&self, server: &Server, path: &str) -> Result<xml::Node, Error> {
        let mut url = server_url(&server.url)?;
        let prefix = url.path().trim_end_matches('/').to_owned();
        let (path, query) = path
            .split_once('?')
            .map_or((path, None), |(path, query)| (path, Some(query)));
        if !path.starts_with('/')
            || path.contains("..")
            || path.contains("//")
            || path.contains('\\')
        {
            return Err(Error::Invalid("invalid Plex server resource"));
        }
        url.set_path(&format!("{prefix}{path}"));
        url.set_query(query);
        let bytes = self
            .send(
                Method::GET,
                url,
                Some(&server.token),
                None,
                "application/xml",
            )
            .await?;
        xml::read(&bytes)
    }
    pub async fn test_server(&self, server: &Server) -> Result<String, Error> {
        let root = self.xml(server, "/identity").await?;
        let id = root
            .text("machineIdentifier")
            .ok_or(Error::Invalid("Plex server identity missing"))?;
        if id != server.id {
            return Err(Error::Invalid(
                "Plex endpoint belongs to a different server",
            ));
        }
        self.libraries(server).await?; // Identity is public on some servers; prove token access.
        Ok(root.text("version").unwrap_or_default().to_owned())
    }
    async fn pages(
        &self,
        server: &Server,
        path: &str,
        tag: &str,
        limit: u32,
    ) -> Result<Vec<xml::Node>, Error> {
        self.pages_window(server, path, tag, limit, None).await
    }
    async fn pages_window(
        &self,
        server: &Server,
        path: &str,
        tag: &str,
        limit: u32,
        window: Option<u32>,
    ) -> Result<Vec<xml::Node>, Error> {
        self.pages_mapped(server, path, tag, limit, window, |node| {
            let bytes = node.bytes();
            Ok((node, bytes))
        })
        .await
    }
    /// Retain only the requested representation while complete-page checks remain shared.
    async fn pages_mapped<T>(
        &self,
        server: &Server,
        path: &str,
        tag: &str,
        limit: u32,
        window: Option<u32>,
        map: impl Fn(xml::Node) -> Result<(T, usize), Error>,
    ) -> Result<Vec<T>, Error> {
        let mut out = Vec::new();
        let mut bytes = 0_usize;
        let mut seen = HashSet::new();
        let mut expected = None;
        let mut start = 0_u32;
        for _ in 0..1000 {
            let separator = if path.contains('?') { '&' } else { '?' };
            let root = self
                .xml(
                    server,
                    &format!(
                        "{path}{separator}X-Plex-Container-Start={start}&X-Plex-Container-Size=100"
                    ),
                )
                .await?;
            if root.name != "MediaContainer" {
                return Err(Error::Invalid("invalid Plex container"));
            }
            let total = root
                .text("totalSize")
                .map(str::parse::<u32>)
                .transpose()
                .map_err(|_| Error::Invalid("invalid Plex snapshot size"))?;
            if total.is_some_and(|total| total > limit) || (start > 0 && total != expected) {
                return Err(Error::Invalid(
                    "Plex snapshot size changed or exceeded its bound",
                ));
            }
            if start == 0 {
                expected = total;
            }
            if root
                .text("offset")
                .map(str::parse::<u32>)
                .transpose()
                .map_err(|_| Error::Invalid("invalid Plex page offset"))?
                .is_some_and(|offset| offset != start)
            {
                return Err(Error::Invalid("Plex returned a different page"));
            }
            let page: Vec<_> = root
                .children
                .into_iter()
                .filter(|n| {
                    n.name == tag
                        || tag == "Media" && matches!(n.name.as_str(), "Video" | "Directory")
                })
                .collect();
            let count =
                u32::try_from(page.len()).map_err(|_| Error::Invalid("Plex page too large"))?;
            if count > 100
                || start.saturating_add(count) > limit
                || total.is_some_and(|n| start.saturating_add(count) > n)
            {
                return Err(Error::Invalid("Plex page exceeds snapshot bounds"));
            }
            for item in page {
                let history = path.starts_with("/status/sessions/history/");
                let event = history
                    .then(|| {
                        item.text("historyKey").map(str::to_owned).or_else(|| {
                            Some(format!(
                                "{}:{}:{}",
                                item.text("ratingKey")?,
                                item.text("accountID")?,
                                item.text("viewedAt")?
                            ))
                        })
                    })
                    .flatten();
                let key = event
                    .or_else(|| {
                        item.text("ratingKey")
                            .or_else(|| item.text("key"))
                            .or_else(|| item.text("id"))
                            .or_else(|| item.text("hubIdentifier"))
                            .map(str::to_owned)
                    })
                    .ok_or(Error::Invalid("Plex snapshot identity missing"))?;
                if !seen.insert(key) {
                    return Err(Error::Invalid("duplicate Plex snapshot record"));
                }
                let (item, retained_bytes) = map(item)?;
                bytes = bytes.saturating_add(retained_bytes);
                if bytes > 128 * 1024 * 1024 {
                    return Err(Error::Invalid("Plex snapshot exceeds memory limit"));
                }
                out.push(item);
            }
            start += count;
            if window.is_some_and(|n| start >= n)
                || total == Some(start)
                || (total.is_none() && count < 100)
            {
                return Ok(out);
            }
            if count == 0 || (count < 100 && total.is_some_and(|total| start < total)) {
                return Err(Error::Invalid("incomplete Plex snapshot"));
            }
        }
        Err(Error::Invalid("Plex snapshot exceeded page limit"))
    }
    pub async fn libraries(&self, server: &Server) -> Result<Vec<Library>, Error> {
        Ok(self
            .pages(server, "/library/sections", "Directory", 2000)
            .await?
            .into_iter()
            .filter_map(|n| {
                let kind = n.text("type")?;
                if !matches!(kind, "movie" | "show") {
                    return None;
                }
                Some(Library {
                    id: n.text("key")?.to_owned(),
                    title: n.text("title").unwrap_or_default().into(),
                    kind: kind.into(),
                })
            })
            .collect())
    }
    /// One bounded browser page; never fetch a whole Plex library for a UI request.
    pub async fn browse_media(
        &self,
        server: &Server,
        library: &Library,
        parent: Option<&str>,
        search: &str,
        offset: u32,
    ) -> Result<crate::MediaPage, Error> {
        identity(&library.id)?;
        if offset > 100_000 || search.len() > 256 {
            return Err(Error::Invalid("Plex browser request exceeds its bounds"));
        }
        let resource = if let Some(parent) = parent {
            identity(parent)?;
            // Verify that the selected show belongs to the requested library.
            let root = self
                .xml(server, &format!("/library/metadata/{parent}"))
                .await?;
            let shows: Vec<_> = root
                .children
                .iter()
                .filter(|n| {
                    n.text("ratingKey") == Some(parent)
                        && n.text("type") == Some("show")
                        && n.text("librarySectionID") == Some(library.id.as_str())
                })
                .collect();
            if shows.len() != 1 {
                return Err(Error::Invalid("Plex show does not belong to this library"));
            }
            format!("/library/metadata/{parent}/allLeaves")
        } else {
            format!("/library/sections/{}/all", library.id)
        };
        let mut query = Url::parse("http://fixture.invalid")
            .map_err(|_| Error::Invalid("invalid Plex browser query"))?;
        query
            .query_pairs_mut()
            .append_pair("X-Plex-Container-Start", &offset.to_string())
            .append_pair("X-Plex-Container-Size", "50")
            .append_pair("sort", "titleSort:asc");
        if !search.is_empty() {
            query.query_pairs_mut().append_pair("title", search);
        }
        let root = self
            .xml(
                server,
                &format!("{resource}?{}", query.query().unwrap_or_default()),
            )
            .await?;
        if root.name != "MediaContainer"
            || root
                .text("offset")
                .is_some_and(|s| s.parse::<u32>() != Ok(offset))
        {
            return Err(Error::Invalid("Plex returned a different browser page"));
        }
        let total = root
            .text("totalSize")
            .map(str::parse::<u32>)
            .transpose()
            .map_err(|_| Error::Invalid("invalid Plex browser total"))?;
        let mut seen = HashSet::new();
        let mut items = Vec::new();
        for node in root
            .children
            .iter()
            .filter(|n| matches!(n.name.as_str(), "Video" | "Directory"))
        {
            if items.len() >= 50 {
                return Err(Error::Invalid("Plex browser page exceeds its bound"));
            }
            let mut item = media(node)?;
            if !seen.insert(item.rating_key.clone()) {
                return Err(Error::Invalid("duplicate Plex browser identity"));
            }
            let files: HashSet<_> = node
                .children("Media")
                .flat_map(|m| m.children("Part"))
                .filter_map(|p| p.text("file"))
                .collect();
            if files.len() != 1 {
                item.file = None;
            }
            items.push(item);
        }
        let count = u32::try_from(items.len())
            .map_err(|_| Error::Invalid("Plex browser page exceeds its bound"))?;
        if total.is_some_and(|t| offset.saturating_add(count) > t) {
            return Err(Error::Invalid("Plex browser total changed"));
        }
        let next =
            (count == 50 && total.is_none_or(|t| offset + count < t)).then_some(offset + count);
        Ok(crate::MediaPage {
            items,
            offset,
            total,
            next,
        })
    }
    pub async fn library_media(
        &self,
        server: &Server,
        library: &Library,
    ) -> Result<Vec<Media>, Error> {
        identity(&library.id)?;
        let path = match library.kind.as_str() {
            "movie" => format!("/library/sections/{}/all?type=1", library.id),
            "show" => format!("/library/sections/{}/allLeaves", library.id),
            _ => return Err(Error::Invalid("unsupported Plex library")),
        };
        self.pages(server, &path, "Video", 100_000)
            .await?
            .iter()
            .map(media)
            .collect()
    }
    pub async fn catalogue(
        &self,
        server: &Server,
        library: &Library,
    ) -> Result<Vec<CatalogueMedia>, Error> {
        identity(&library.id)?;
        let path = match library.kind.as_str() {
            "movie" => format!("/library/sections/{}/all?type=1", library.id),
            "show" => format!("/library/sections/{}/allLeaves", library.id),
            _ => return Err(Error::Invalid("unsupported Plex library")),
        };
        self.pages_mapped(server, &path, "Video", 100_000, None, |node| {
            let mut files = std::collections::BTreeSet::new();
            let mut file_sizes = std::collections::HashMap::new();
            for part in node.children("Media").flat_map(|m| m.children("Part")) {
                if let Some(path) = part.text("file") {
                    if !crate::safe_path(path) {
                        return Err(Error::Invalid("invalid Plex catalogue path"));
                    }
                    files.insert(path.to_owned());
                    if let Some(size) = part.number::<u64>("size") {
                        file_sizes.insert(path.to_owned(), size);
                    }
                    if files.len() > 64 {
                        return Err(Error::Invalid("too many Plex media parts"));
                    }
                }
            }
            let entry = CatalogueMedia {
                media: media(&node)?,
                files: files.into_iter().collect(),
                file_sizes,
                year: node.number("year"),
            };
            // Full XML includes cast, tags and stream details which this catalogue
            // does not use. Drop each page's tree after extracting compact rows.
            let bytes =
                serde_json::to_vec(&(&entry.media, &entry.files, entry.year, &entry.file_sizes))
                    .map_err(|_| Error::Invalid("cannot size Plex catalogue"))?
                    .len();
            Ok((entry, bytes))
        })
        .await
    }

    pub async fn metadata(&self, server: &Server, key: &str) -> Result<Media, Error> {
        identity(key)?;
        let root = self
            .xml(server, &format!("/library/metadata/{key}"))
            .await?;
        let items: Vec<_> = root
            .children
            .iter()
            .filter(|n| matches!(n.name.as_str(), "Video" | "Directory"))
            .collect();
        if items.len() != 1 {
            return Err(Error::Invalid("Plex metadata identity is ambiguous"));
        }
        let item = media(items[0])?;
        if item.rating_key != key {
            return Err(Error::Invalid("Plex metadata identity changed"));
        }
        Ok(item)
    }
    pub async fn next_episodes(
        &self,
        viewer: &ViewerServer,
        show: &str,
    ) -> Result<Vec<Media>, Error> {
        identity(show)?;
        let mut items:Vec<_>=self.pages(&viewer.server,&format!("/library/metadata/{show}/allLeaves?unwatched=1&sort=parentIndex%3Aasc%2Cindex%3Aasc"),"Video",100_000).await?.iter().map(media).collect::<Result<_,_>>()?;
        for item in &mut items {
            item.user = Some(viewer.viewer.clone());
        }
        Ok(items)
    }
    /// Discover named candidate sources using this exact viewer credential.
    pub async fn source_catalogue(
        &self,
        viewer: &ViewerServer,
        library: Option<&Library>,
    ) -> Result<Vec<crate::CatalogueSource>, Error> {
        let server = &viewer.server;
        let mut result = Vec::new();
        let path = if let Some(l) = library {
            identity(&l.id)?;
            format!("/hubs/sections/{}", l.id)
        } else {
            "/hubs".into()
        };
        for node in self.pages(server, &path, "Hub", 2000).await? {
            let Some(path) = node.text("key") else {
                continue;
            };
            if crate::sources::resource(path).is_err() {
                continue;
            }
            result.push(crate::CatalogueSource {
                title: node.text("title").unwrap_or("Plex hub").into(),
                kind: crate::sources::Kind::Hub,
                path: path.into(),
                library: library.map(|l| l.id.clone()),
                media_kind: library.map_or_else(
                    || node.text("type").unwrap_or("").into(),
                    |l| l.kind.clone(),
                ),
            });
        }
        if let Some(l) = library {
            for node in self
                .pages(
                    server,
                    &format!("/library/sections/{}/collections", l.id),
                    "Directory",
                    2000,
                )
                .await?
            {
                let Some(id) = node.text("ratingKey") else {
                    continue;
                };
                identity(id)?;
                result.push(crate::CatalogueSource {
                    title: node.text("title").unwrap_or("Plex collection").into(),
                    kind: crate::sources::Kind::Collection,
                    path: format!("/library/metadata/{id}/children"),
                    library: Some(l.id.clone()),
                    media_kind: l.kind.clone(),
                });
            }
            result.push(crate::CatalogueSource {
                title: format!("All {}", l.title),
                kind: crate::sources::Kind::Library,
                path: format!(
                    "/library/sections/{}/all?type={}",
                    l.id,
                    if l.kind == "movie" { 1 } else { 2 }
                ),
                library: Some(l.id.clone()),
                media_kind: l.kind.clone(),
            });
        }
        let mut seen = HashSet::new();
        result.retain(|s| seen.insert((s.kind as u8, s.path.clone())));
        Ok(result)
    }
    /// Fetch the explicitly selected ordered prefix with the exact viewer token.
    /// Changed totals, duplicate identities and partial pages fail the whole pass.
    pub async fn prefetch_source(
        &self,
        viewer: &ViewerServer,
        source: &crate::sources::Source,
    ) -> Result<Vec<Media>, Error> {
        source.validate()?;
        if source.server != viewer.server.id || source.viewer != viewer.viewer {
            return Err(Error::Unauthorized);
        }
        if !source.enabled {
            return Ok(Vec::new());
        }
        let nodes = self
            .pages_window(
                &viewer.server,
                &source.path,
                "Media",
                100_000,
                Some(u32::from(source.limit)),
            )
            .await?;
        nodes
            .iter()
            .take(usize::from(source.limit))
            .map(|node| {
                let mut item = media(node)?;
                item.user = Some(viewer.viewer.clone());
                Ok(item)
            })
            .collect()
    }
    /// Optional artwork stays on the configured Plex server and never carries tokens in a URL.
    pub async fn artwork(
        &self,
        server: &Server,
        key: &str,
    ) -> Result<(Vec<u8>, &'static str), Error> {
        identity(key)?;
        let root = self
            .xml(server, &format!("/library/metadata/{key}"))
            .await?;
        let node = root
            .children
            .iter()
            .find(|n| matches!(n.name.as_str(), "Video" | "Directory"))
            .ok_or(Error::Invalid("Plex artwork metadata is missing"))?;
        let path = node
            .text("thumb")
            .or_else(|| node.text("grandparentThumb"))
            .ok_or(Error::Invalid("Plex artwork is unavailable"))?;
        if node.text("ratingKey") != Some(key)
            || !path.starts_with("/library/metadata/")
            || path.contains(['?', '#', '\\', '%'])
            || path.contains("//")
            || path.chars().any(char::is_control)
            || path.contains("..")
        {
            return Err(Error::Invalid("Invalid Plex artwork resource"));
        }
        let mut url = server_url(&server.url)?;
        let prefix = url.path().trim_end_matches('/').to_owned();
        url.set_path(&format!("{prefix}{path}"));
        url.set_query(None);
        let bytes = self
            .send(
                Method::GET,
                url,
                Some(&server.token),
                None,
                "image/jpeg,image/png,image/webp",
            )
            .await?;
        let content_type = if bytes.starts_with(&[255, 216, 255]) {
            "image/jpeg"
        } else if bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
            "image/png"
        } else if bytes.starts_with(b"RIFF") && bytes.get(8..12) == Some(b"WEBP") {
            "image/webp"
        } else {
            return Err(Error::Invalid("Unsupported Plex artwork"));
        };
        Ok((bytes, content_type))
    }
    /// Revalidate an explicitly selected Part; never substitute metadata's first version.
    pub async fn metadata_variant(
        &self,
        server: &Server,
        key: &str,
        path: &str,
    ) -> Result<Media, Error> {
        identity(key)?;
        let root = self
            .xml(server, &format!("/library/metadata/{key}"))
            .await?;
        let nodes: Vec<_> = root
            .children
            .iter()
            .filter(|n| matches!(n.name.as_str(), "Video" | "Directory"))
            .collect();
        let [node] = nodes.as_slice() else {
            return Err(Error::Invalid("Plex media identity is ambiguous"));
        };
        let mut result = media(node)?;
        if result.rating_key != key
            || !node
                .children("Media")
                .flat_map(|m| m.children("Part"))
                .any(|p| p.text("file") == Some(path))
        {
            return Err(Error::Invalid(
                "Selected Plex version changed; refresh the library",
            ));
        }
        result.file = Some(path.to_owned());
        Ok(result)
    }
    pub async fn viewer_metadata(&self, viewer: &ViewerServer, key: &str) -> Result<Media, Error> {
        let mut item = self.metadata(&viewer.server, key).await?;
        item.user = Some(viewer.viewer.clone());
        Ok(item)
    }
    pub async fn sessions(&self, server: &Server) -> Result<Vec<Session>, Error> {
        let root = self.xml(server, "/status/sessions").await?;
        let mut result = Vec::new();
        for node in root.children.iter().filter(|n| n.name == "Video") {
            if result.len() >= 2000 {
                return Err(Error::Invalid("too many Plex sessions"));
            }
            let account = node
                .children("User")
                .next()
                .ok_or(Error::Invalid("Plex session viewer missing"))?;
            let user = account
                .text("id")
                .ok_or(Error::Invalid("Plex session viewer missing"))?
                .to_owned();
            identity(&user)?;
            let player = node
                .children("Player")
                .next()
                .ok_or(Error::Invalid("Plex session player missing"))?;
            result.push(Session {
                id: node
                    .children("Session")
                    .next()
                    .and_then(|n| n.text("id"))
                    .unwrap_or_default()
                    .into(),
                user: user.clone(),
                user_name: account.text("title").map(str::to_owned),
                player: player.text("title").map(str::to_owned),
                state: player.text("state").unwrap_or_default().into(),
                media: Media {
                    user: Some(user),
                    ..media(node)?
                },
            });
        }
        Ok(result)
    }
    /// A complete newest-first window, rather than an unbounded lifetime history.
    /// The planner's policy considers at most these 1000 distinct history records.
    pub async fn recent_history(&self, server: &Server) -> Result<Vec<Media>, Error> {
        Ok(self.recent_history_report(server).await?.0)
    }
    /// Historical events can outlive their media metadata. Never fabricate an ID.
    pub async fn recent_history_report(
        &self,
        server: &Server,
    ) -> Result<(Vec<Media>, usize), Error> {
        let nodes = self
            .pages_window(
                server,
                "/status/sessions/history/all?sort=viewedAt%3Adesc",
                "Video",
                u32::MAX,
                Some(1000),
            )
            .await?;
        let mut missing = 0;
        let mut items = Vec::new();
        for node in &nodes {
            if node.text("ratingKey").is_none_or(str::is_empty) {
                missing += 1;
                continue;
            }
            items.push(media(node)?);
        }
        Ok((items, missing))
    }
    pub async fn history(&self, server: &Server) -> Result<Vec<Media>, Error> {
        self.pages(
            server,
            "/status/sessions/history/all?sort=viewedAt%3Adesc",
            "Video",
            100_000,
        )
        .await?
        .iter()
        .map(media)
        .collect()
    }
    /// Account identity and this machine's access are verified on every refresh pass.
    pub async fn viewer(
        &self,
        server: &Server,
        viewer: &str,
        accounts: &[Account],
    ) -> Result<ViewerServer, Error> {
        identity(viewer)?;
        let mut unavailable = false;
        for saved in accounts.iter().take(16) {
            let account = match self.account(&saved.token).await {
                Ok(account) => account,
                Err(Error::Unavailable | Error::Timeout | Error::RateLimited) => {
                    unavailable = true;
                    continue;
                }
                Err(_) => continue,
            };
            if account.id != saved.id && account.numeric_id.as_deref() != Some(saved.id.as_str()) {
                continue;
            }
            let resources = match self.discover(&saved.token).await {
                Ok(resources) => resources,
                Err(Error::Unavailable | Error::Timeout | Error::RateLimited) => {
                    unavailable = true;
                    continue;
                }
                Err(_) => continue,
            };
            let Some(resource) = resources.into_iter().find(|r| r.id == server.id) else {
                continue;
            };
            let matches = if viewer == "1" {
                resource.owned
            } else {
                account.id == viewer || account.numeric_id.as_deref() == Some(viewer)
            };
            if matches {
                let scoped = Server {
                    token: resource.token,
                    account_id: Some(viewer.into()),
                    ..server.clone()
                };
                self.test_server(&scoped).await?;
                return Ok(ViewerServer {
                    server: scoped,
                    viewer: viewer.to_owned(),
                    viewer_name: account.name,
                });
            }
        }
        Err(if unavailable {
            Error::Unavailable
        } else {
            Error::Unauthorized
        })
    }
}
pub(crate) fn server_url(raw: &str) -> Result<Url, Error> {
    let url = Url::parse(raw).map_err(|_| Error::Invalid("invalid Plex server URL"))?;
    if raw.len() > 2048
        || raw.chars().any(char::is_control)
        || !matches!(url.scheme(), "http" | "https")
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(Error::Invalid(
            "Plex server URL must be HTTP(S) without credentials, query or fragment",
        ));
    }
    Ok(url)
}
fn encode(text: &str) -> String {
    percent_encoding::utf8_percent_encode(text, percent_encoding::NON_ALPHANUMERIC).to_string()
}
fn text(value: &Value, key: &str) -> Option<String> {
    value
        .get(key)?
        .as_str()
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
}
fn scalar(value: &Value, key: &str) -> Option<String> {
    text(value, key).or_else(|| value.get(key)?.as_i64().map(|n| n.to_string()))
}
fn boolean(value: &Value, key: &str) -> bool {
    value
        .get(key)
        .is_some_and(|v| v.as_bool() == Some(true) || v.as_i64() == Some(1))
}
fn pin(value: &Value) -> Result<Pin, Error> {
    Ok(Pin {
        id: value
            .get("id")
            .and_then(Value::as_u64)
            .ok_or(Error::Invalid("Plex PIN identity missing"))?,
        code: text(value, "code").unwrap_or_default(),
        expires_in: value.get("expiresIn").and_then(Value::as_u64).unwrap_or(0),
        token: text(value, "authToken").map(Token),
    })
}
fn media(node: &xml::Node) -> Result<Media, Error> {
    let rating_key = node
        .text("ratingKey")
        .ok_or(Error::Invalid("Plex media identity missing"))?
        .to_owned();
    identity(&rating_key)?;
    let file = node
        .children("Media")
        .flat_map(|m| m.children("Part"))
        .find_map(|p| p.text("file"))
        .map(str::to_owned);
    let resolutions: std::collections::HashSet<_> = node
        .children("Media")
        .filter_map(|m| m.text("videoResolution"))
        .collect();
    Ok(Media {
        artwork: node
            .text("thumb")
            .or_else(|| node.text("grandparentThumb"))
            .is_some(),
        year: node.number("year"),
        resolution: (resolutions.len() == 1)
            .then(|| resolutions.into_iter().next().map(str::to_owned))
            .flatten(),
        rating_key,
        kind: node.text("type").unwrap_or_default().into(),
        title: node.text("title").unwrap_or_default().into(),
        show: node.text("grandparentRatingKey").map(str::to_owned),
        show_title: node.text("grandparentTitle").map(str::to_owned),
        season: node.number("parentIndex"),
        episode: node.number("index"),
        file,
        view_count: node
            .text("viewCount")
            .map(str::parse::<u32>)
            .transpose()
            .map_err(|_| Error::Invalid("invalid Plex watched state"))?
            .unwrap_or(0),
        view_offset_ms: node.number("viewOffset").unwrap_or(0),
        duration_ms: node.number("duration").unwrap_or(0),
        viewed_at: node.number("viewedAt"),
        user: node.text("accountID").map(str::to_owned),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use axum::Router;
    use axum::body::Body;
    use axum::extract::State;
    use axum::http::{HeaderMap, Response, Uri};
    use std::fmt::Write as _;
    use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
    use std::sync::{Arc, Mutex};
    type Handler = Arc<dyn Fn(Uri, HeaderMap) -> Response<Body> + Send + Sync>;
    struct Fixture {
        client: Client,
        server: Server,
        task: tokio::task::JoinHandle<()>,
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            self.task.abort();
        }
    }
    async fn fixture(
        handler: impl Fn(Uri, HeaderMap) -> Response<Body> + Send + Sync + 'static,
    ) -> Fixture {
        async fn request(
            State(handler): State<Handler>,
            uri: Uri,
            headers: HeaderMap,
        ) -> Response<Body> {
            handler(uri, headers)
        }
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .expect("listener");
        let addr = listener.local_addr().expect("address");
        let handler: Handler = Arc::new(handler);
        let router = Router::new().fallback(request).with_state(handler);
        let task = tokio::spawn(async move {
            let _ = axum::serve(listener, router).await;
        });
        let mut client = Client::new(Uuid::new_v4()).expect("HTTP client");
        client.cloud = Url::parse(&format!("http://{addr}/")).expect("fixture URL");
        let server = Server {
            id: "machine".into(),
            name: "fixture".into(),
            url: format!("http://{addr}/prefix"),
            token: Token("fixture-server-token".into()),
            enabled: true,
            account_id: None,
            path_mappings: vec![],
        };
        Fixture {
            client,
            server,
            task,
        }
    }
    fn response(code: u16, body: impl Into<Body>) -> Response<Body> {
        Response::builder()
            .status(code)
            .body(body.into())
            .expect("response")
    }
    fn xml_page(start: u32, total: u32, ids: impl IntoIterator<Item = u32>) -> String {
        let mut xml = format!("<MediaContainer offset='{start}' totalSize='{total}'>");
        for id in ids {
            write!(xml,"<Video ratingKey='{id}' type='movie' title='fixture'><Media><Part file='/library/fixture-{id}.dat'/></Media></Video>").expect("string");
        }
        xml.push_str("</MediaContainer>");
        xml
    }
    #[tokio::test]
    async fn compact_catalogue_rejects_partial_or_duplicate_late_pages() {
        for duplicate in [false, true] {
            let f = fixture(move |uri, _| {
                let first = !uri
                    .query()
                    .unwrap_or_default()
                    .contains("X-Plex-Container-Start=100");
                let start = if first { 0 } else { 100 };
                let mut xml = format!("<MediaContainer totalSize='101' offset='{start}'>");
                let count = if first { 100 } else { usize::from(duplicate) };
                for i in 0..count {
                    write!(xml, "<Video ratingKey='{i}' type='movie' title='Fixture'/>").unwrap();
                }
                xml.push_str("</MediaContainer>");
                response(200, xml)
            })
            .await;
            let library = Library {
                id: "7".into(),
                title: "Fixture".into(),
                kind: "movie".into(),
            };
            let result = f.client.catalogue(&f.server, &library).await;
            assert!(matches!(result, Err(Error::Invalid(_))));
        }
    }

    #[tokio::test]
    async fn catalogue_discards_unused_xml_without_raising_snapshot_bounds() {
        const TOTAL: u32 = 1_200;
        let unused = "<Genre tag='fixture'/>".repeat(800);
        let f=fixture(move |uri,_| {
            let start=uri.query().unwrap_or_default().split('&').find_map(|part| part.strip_prefix("X-Plex-Container-Start=")).unwrap_or("0").parse::<u32>().unwrap();
            let mut xml=format!("<MediaContainer totalSize='{TOTAL}' offset='{start}'>");
            for i in start..(start+100).min(TOTAL) {
                write!(xml,"<Video ratingKey='{i}' type='episode' title='Fixture' grandparentRatingKey='show' parentIndex='1' index='{i}'><Media><Part file='/fixture/{i}.mkv' size='100'/></Media>{unused}</Video>").unwrap();
            }
            xml.push_str("</MediaContainer>");response(200,xml)
        }).await;
        // The ordinary XML collector still enforces its original aggregate bound.
        assert!(matches!(
            f.client
                .pages(&f.server, "/library/sections/7/allLeaves", "Video", 100_000)
                .await,
            Err(Error::Invalid("Plex snapshot exceeds memory limit"))
        ));
        let library = Library {
            id: "7".into(),
            title: "Fixture library".into(),
            kind: "show".into(),
        };
        let rows = f.client.catalogue(&f.server, &library).await.unwrap();
        assert_eq!(rows.len(), usize::try_from(TOTAL).unwrap());
        assert_eq!(rows[1_199].media.rating_key, "1199");
        assert_eq!(rows[1_199].files, ["/fixture/1199.mkv"]);
        assert_eq!(rows[1_199].file_sizes["/fixture/1199.mkv"], 100);
    }

    #[tokio::test]
    async fn exact_part_selection_revalidates_current_metadata() {
        let f=fixture(|_,_| response(200,"<MediaContainer><Video ratingKey='42' type='movie' title='Fixture'><Media videoResolution='1080'><Part file='/fixture/first.mkv'/><Part file='/fixture/second.mkv'/></Media></Video></MediaContainer>")).await;
        let media = f
            .client
            .metadata_variant(&f.server, "42", "/fixture/second.mkv")
            .await
            .unwrap();
        assert_eq!(media.file.as_deref(), Some("/fixture/second.mkv"));
        assert!(
            f.client
                .metadata_variant(&f.server, "42", "/fixture/gone.mkv")
                .await
                .is_err()
        );
        assert!(
            f.client
                .metadata_variant(&f.server, "43", "/fixture/second.mkv")
                .await
                .is_err()
        );
    }
    #[tokio::test]
    async fn artwork_rejects_untrusted_resource_before_fetching_it() {
        for resource in [
            "https://example.invalid/thumb",
            "/library/metadata/42/%2e%2e/private",
            "/library/metadata/42/thumb?token=secret",
        ] {
            let xml = format!(
                "<MediaContainer><Video ratingKey='42' type='movie' title='Fixture' thumb='{resource}'/></MediaContainer>"
            );
            let f = fixture(move |uri, _| {
                assert!(uri.path().ends_with("/library/metadata/42"));
                response(200, xml.clone())
            })
            .await;
            assert!(f.client.artwork(&f.server, "42").await.is_err());
        }
    }
    #[tokio::test]
    async fn identity_requires_protected_access_and_credentials_stay_out_of_urls() {
        let seen = Arc::new(Mutex::new(Vec::new()));
        let log = Arc::clone(&seen);
        let f = fixture(move |uri, headers| {
            assert!(!uri.to_string().contains("token"));
            assert_eq!(
                headers.get("X-Plex-Token").and_then(|h| h.to_str().ok()),
                Some("fixture-server-token")
            );
            assert_eq!(
                headers.get(ACCEPT).and_then(|h| h.to_str().ok()),
                Some("application/xml")
            );
            log.lock().expect("log").push(uri.path().to_owned());
            if uri.path().ends_with("/identity") {
                response(
                    200,
                    "<MediaContainer machineIdentifier='machine' version='1'/>",
                )
            } else {
                response(401, "denied")
            }
        })
        .await;
        assert!(matches!(
            f.client.test_server(&f.server).await,
            Err(Error::Unauthorized)
        ));
        assert_eq!(seen.lock().expect("log").len(), 2);
        assert!(!format!("{:?}", f.server).contains("fixture-server-token"));
    }
    #[tokio::test]
    async fn wrong_machine_stops_before_library_queries() {
        let calls = Arc::new(AtomicUsize::new(0));
        let count = Arc::clone(&calls);
        let f = fixture(move |_, _| {
            count.fetch_add(1, Ordering::SeqCst);
            response(200, "<MediaContainer machineIdentifier='different'/>")
        })
        .await;
        assert!(matches!(
            f.client.test_server(&f.server).await,
            Err(Error::Invalid(_))
        ));
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }
    #[tokio::test]
    async fn redirects_are_not_followed_with_credentials() {
        let calls = Arc::new(AtomicUsize::new(0));
        let count = Arc::clone(&calls);
        let target = fixture(move |_, _| {
            count.fetch_add(1, Ordering::SeqCst);
            response(200, "[]")
        })
        .await;
        let url = target.client.cloud.to_string();
        let f = fixture(move |_, _| {
            let mut r = response(302, "");
            r.headers_mut()
                .insert("location", HeaderValue::from_str(&url).expect("URL"));
            r
        })
        .await;
        assert!(matches!(
            f.client
                .discover(&Token("fixture-account-token".into()))
                .await,
            Err(Error::Unavailable)
        ));
        assert_eq!(calls.load(Ordering::SeqCst), 0);
    }
    #[tokio::test]
    async fn complete_library_pages_are_required() {
        for failure in ["empty", "changed", "duplicate", "short"] {
            let f = fixture(move |uri, _| {
                let second = uri.query().unwrap_or_default().contains("Start=100");
                let xml = if second {
                    match failure {
                        "empty" => xml_page(100, 101, []),
                        "changed" => xml_page(100, 102, [100]),
                        _ => xml_page(100, 101, [0]),
                    }
                } else if failure == "short" {
                    xml_page(0, 101, [0])
                } else {
                    xml_page(0, 101, 0..100)
                };
                response(200, xml)
            })
            .await;
            assert!(
                matches!(
                    f.client
                        .library_media(
                            &f.server,
                            &Library {
                                id: "1".into(),
                                title: "fixture".into(),
                                kind: "movie".into()
                            }
                        )
                        .await,
                    Err(Error::Invalid(_))
                ),
                "{failure}"
            );
        }
    }
    #[tokio::test]
    async fn a_complete_library_keeps_exact_file_paths() {
        let f = fixture(|uri, _| {
            response(
                200,
                if uri.query().unwrap_or_default().contains("Start=100") {
                    xml_page(100, 101, [100])
                } else {
                    xml_page(0, 101, 0..100)
                },
            )
        })
        .await;
        let items = f
            .client
            .library_media(
                &f.server,
                &Library {
                    id: "1".into(),
                    title: "fixture".into(),
                    kind: "movie".into(),
                },
            )
            .await
            .expect("complete snapshot");
        assert_eq!(items.len(), 101);
        assert_eq!(items[100].file.as_deref(), Some("/library/fixture-100.dat"));
    }
    #[tokio::test]
    async fn oversized_responses_fail_before_metadata_can_be_used() {
        let f = fixture(|_, _| response(200, vec![b'x'; LIMIT + 1])).await;
        assert!(matches!(
            f.client.libraries(&f.server).await,
            Err(Error::Invalid(_))
        ));
    }
    #[tokio::test]
    async fn names_and_unowned_resources_never_authorize_the_local_owner() {
        let owned = Arc::new(AtomicBool::new(false));
        let ownership = Arc::clone(&owned);
        let f=fixture(move|uri,headers|{
            match uri.path(){
                "/api/v2/user"=>response(200,r#"{"uuid":"actual","id":42,"username":"viewer"}"#),
                "/api/v2/resources"=>response(200,serde_json::json!([{"clientIdentifier":"machine","provides":"server","name":"fixture","owned":ownership.load(Ordering::SeqCst),"accessToken":"scoped-fixture-token","connections":[]}]).to_string()),
                "/prefix/identity"=>response(200,"<MediaContainer machineIdentifier='machine'/>"),
                "/prefix/library/sections"=>response(200,"<MediaContainer size='0'/>"),
                "/prefix/library/metadata/show/allLeaves"=>{
                    assert!(uri.query().unwrap_or_default().contains("unwatched=1"));
                    assert_eq!(headers.get("X-Plex-Token").and_then(|h|h.to_str().ok()),Some("scoped-fixture-token"));
                    response(200,"<MediaContainer totalSize='1'><Video ratingKey='episode' type='episode' grandparentRatingKey='show' parentIndex='1' index='2'/></MediaContainer>")
                },_=>response(404,"")
            }
        }).await;
        let mut account = Account {
            id: "claimed".into(),
            name: "viewer".into(),
            token: Token("fixture-account-token".into()),
            numeric_id: Some("42".into()),
        };
        assert!(matches!(
            f.client.viewer(&f.server, "1", &[account.clone()]).await,
            Err(Error::Unauthorized)
        ));
        account.id = "actual".into();
        assert!(matches!(
            f.client.viewer(&f.server, "1", &[account.clone()]).await,
            Err(Error::Unauthorized)
        ));
        owned.store(true, Ordering::SeqCst);
        let viewer = f
            .client
            .viewer(&f.server, "1", &[account])
            .await
            .expect("authenticated owner");
        assert_eq!(viewer.viewer_id(), "1");
        assert_eq!(viewer.server_id(), "machine");
        let episodes = f
            .client
            .next_episodes(&viewer, "show")
            .await
            .expect("scoped episodes");
        assert_eq!(episodes[0].user.as_deref(), Some("1"));
    }
    #[tokio::test]
    async fn home_switch_rejects_a_different_returned_identity() {
        let f = fixture(|uri, _| {
            if uri.path().ends_with("/switch") {
                response(200, r#"{"authToken":"fixture-switched-token"}"#)
            } else {
                response(200, r#"{"uuid":"different","id":9,"username":"fixture"}"#)
            }
        })
        .await;
        assert!(matches!(
            f.client
                .switch_home(
                    &Token("fixture-account-token".into()),
                    "profile",
                    Some("1234")
                )
                .await,
            Err(Error::Invalid(_))
        ));
        assert!(matches!(
            f.client
                .switch_home(
                    &Token("fixture-account-token".into()),
                    "profile",
                    Some("12x4")
                )
                .await,
            Err(Error::Invalid(_))
        ));
    }
    #[test]
    fn xml_rejects_dtds_duplicate_attributes_and_ambiguous_roots() {
        for bytes in [
            b"<!DOCTYPE root [<!ENTITY xxe SYSTEM 'file:///fixture'>]><MediaContainer/>".as_slice(),
            b"<MediaContainer a='1' a='2'/>",
            b"<MediaContainer/><MediaContainer/>",
        ] {
            assert!(xml::read(bytes).is_err());
        }
        let root = xml::read(
            b"<MediaContainer title='fixture &amp; item'><Directory key='1'/></MediaContainer>",
        )
        .expect("safe XML");
        assert_eq!(root.text("title"), Some("fixture & item"));
        assert_eq!(root.children.len(), 1);
    }
    #[tokio::test]
    async fn recent_history_is_a_complete_bounded_window() {
        let calls = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let called = Arc::clone(&calls);
        let f = fixture(move |uri, _| {
            called.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            let start = uri
                .query()
                .unwrap_or_default()
                .split('&')
                .find_map(|v| v.strip_prefix("X-Plex-Container-Start="))
                .unwrap()
                .parse::<u32>()
                .unwrap();
            response(200, xml_page(start, 50_000, start..start + 100))
        })
        .await;
        let items = f
            .client
            .recent_history(&f.server)
            .await
            .expect("bounded complete window");
        assert_eq!(items.len(), 1000);
        assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 10);
    }
    #[tokio::test]
    async fn history_rewatches_preserve_distinct_event_identity() {
        let f=fixture(|_,_|response(200,r#"<MediaContainer totalSize="2" offset="0"><Video ratingKey="1" accountID="7" viewedAt="1000"/><Video ratingKey="1" accountID="7" viewedAt="1001"/></MediaContainer>"#)).await;
        assert_eq!(
            f.client
                .recent_history(&f.server)
                .await
                .expect("rewatch events")
                .len(),
            2
        );
    }
    #[tokio::test]
    async fn malformed_watched_state_cannot_be_treated_as_unwatched() {
        let f = fixture(|_, _| {
            response(
                200,
                r#"<MediaContainer><Video ratingKey="1" viewCount="unknown"/></MediaContainer>"#,
            )
        })
        .await;
        assert!(matches!(
            f.client.metadata(&f.server, "1").await,
            Err(Error::Invalid(_))
        ));
    }
    #[tokio::test]
    async fn selected_hub_and_library_prefixes_use_only_verified_viewer_credentials() {
        let f = fixture(|uri, headers| {
            assert_eq!(
                headers.get("X-Plex-Token").and_then(|v| v.to_str().ok()),
                Some("viewer-source-token")
            );
            assert!(matches!(
                uri.path(),
                "/prefix/hubs/home/continueWatching" | "/prefix/library/sections/1/all"
            ));
            response(200, xml_page(0, 2, [1, 2]))
        })
        .await;
        let viewer = ViewerServer {
            server: Server {
                token: Token("viewer-source-token".into()),
                ..f.server.clone()
            },
            viewer: "7".into(),
            viewer_name: "Fixture viewer".into(),
        };
        for (kind, path) in [
            (crate::sources::Kind::Hub, "/hubs/home/continueWatching"),
            (crate::sources::Kind::Library, "/library/sections/1/all"),
        ] {
            let source = crate::sources::Source {
                server: "machine".into(),
                viewer: "7".into(),
                kind,
                path: path.into(),
                limit: 1,
                ..Default::default()
            };
            let items = f
                .client
                .prefetch_source(&viewer, &source)
                .await
                .expect("source");
            assert_eq!(items.len(), 1);
            assert_eq!(items[0].user.as_deref(), Some("7"));
            let wrong = crate::sources::Source {
                viewer: "8".into(),
                ..source
            };
            assert!(matches!(
                f.client.prefetch_source(&viewer, &wrong).await,
                Err(Error::Unauthorized)
            ));
        }
    }

    #[tokio::test]
    async fn history_without_media_ids_does_not_block_actionable_records() {
        let f=fixture(|_,_|response(200,"<MediaContainer totalSize='2' offset='0'><Video historyKey='1' ratingKey='10' type='movie' title='fixture'/><Video historyKey='2' type='movie' title='unavailable fixture'/></MediaContainer>")).await;
        let (items, unavailable) = f.client.recent_history_report(&f.server).await.unwrap();
        assert_eq!(unavailable, 1);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].rating_key, "10");
        // Current media metadata remains strict: only historical events may lack identity.
        assert!(f.client.metadata(&f.server, "10").await.is_err());
    }
    #[tokio::test]
    async fn browser_page_keeps_search_bound_and_does_not_guess_multiple_versions() {
        let f=fixture(|uri,_|{
            assert!(uri.query().unwrap_or_default().contains("title=fixture%26extra%3D1"));
            response(200,"<MediaContainer totalSize='1' offset='0'><Video ratingKey='10' type='movie' title='fixture'><Media><Part file='/fixture/first'/></Media><Media><Part file='/fixture/second'/></Media></Video></MediaContainer>")
        }).await;
        let library = Library {
            id: "7".into(),
            title: "fixture".into(),
            kind: "movie".into(),
        };
        let page = f
            .client
            .browse_media(&f.server, &library, None, "fixture&extra=1", 0)
            .await
            .unwrap();
        assert_eq!(page.total, Some(1));
        assert!(page.next.is_none());
        assert!(page.items[0].file.is_none());
    }
    #[tokio::test]
    async fn browser_rejects_show_from_another_library_before_fetching_episodes() {
        let f=fixture(|uri,_|{assert!(uri.path().ends_with("/library/metadata/42"));response(200,"<MediaContainer><Directory ratingKey='42' type='show' librarySectionID='8'/></MediaContainer>")}).await;
        let library = Library {
            id: "7".into(),
            title: "fixture".into(),
            kind: "show".into(),
        };
        assert!(
            f.client
                .browse_media(&f.server, &library, Some("42"), "", 0)
                .await
                .is_err()
        );
    }
    #[tokio::test]
    async fn named_catalogue_keeps_hubs_and_canonical_collection_identity() {
        let f=fixture(|uri,_|{
            if uri.path().ends_with("/hubs/sections/7") {response(200,"<MediaContainer><Hub key='/library/sections/7/recentlyAdded' title='fixture hub'/></MediaContainer>")}
            else {assert!(uri.path().ends_with("/library/sections/7/collections"));response(200,"<MediaContainer><Directory ratingKey='44' type='collection' title='fixture collection'/></MediaContainer>")}
        }).await;
        let viewer = ViewerServer {
            server: f.server.clone(),
            viewer: "1".into(),
            viewer_name: "Fixture owner".into(),
        };
        let library = Library {
            id: "7".into(),
            title: "fixture".into(),
            kind: "movie".into(),
        };
        let sources = f
            .client
            .source_catalogue(&viewer, Some(&library))
            .await
            .unwrap();
        assert_eq!(sources.len(), 3);
        assert_eq!(sources[1].path, "/library/metadata/44/children");
        crate::sources::Source {
            server: "machine".into(),
            viewer: "1".into(),
            kind: sources[0].kind,
            path: sources[0].path.clone(),
            ..Default::default()
        }
        .validate()
        .unwrap();
    }
}
