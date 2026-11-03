use crate::{
    Account, Connection, HomeUser, Library, Media, Resource, Server, Session, Token, ViewerServer,
    identity, secret, xml,
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
                .filter(|n| n.name == tag)
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
                bytes = bytes.saturating_add(item.bytes());
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
        self.pages_window(
            server,
            "/status/sessions/history/all?sort=viewedAt%3Adesc",
            "Video",
            u32::MAX,
            Some(1000),
        )
        .await?
        .iter()
        .map(media)
        .collect()
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
    Ok(Media {
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
}
