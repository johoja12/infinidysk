//! Administrator-only Plex connection handles. Credentials remain behind the API.
use crate::admin::{Admin, Fail};
use crate::config::Secret;
use axum::{
    Json,
    extract::{Path, State},
    http::HeaderMap,
};
use infinidysk_db::{Db, now_ms};
use infinidysk_plex::{Account, Client, Connection, HomeUser, Library, Resource, Server};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::fmt::Write as _;
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use tokio::sync::Mutex as AsyncMutex;
use utoipa::ToSchema;
use uuid::Uuid;

type Logins = Arc<Mutex<HashMap<String, Arc<Login>>>>;
struct Login {
    owner: String,
    expires: AtomicI64,
    cancelled: AtomicBool,
    state: AsyncMutex<LoginState>,
}
#[derive(Default)]
struct LoginState {
    pin: Option<u64>,
    connected: bool,
    cancelled: bool,
}
struct Discovered {
    owner: String,
    account: String,
    credential: [u8; 32],
    resource: Resource,
    expires: i64,
}
pub struct Service {
    pub(crate) client: Client,
    logins: Logins,
    discovered: Mutex<HashMap<String, Discovered>>,
}
impl Service {
    pub fn invalidate_pending(&self) {
        let mut logins = self.logins.lock().unwrap_or_else(PoisonError::into_inner);
        for login in logins.values() {
            login.cancelled.store(true, Ordering::SeqCst);
        }
        logins.clear();
        drop(logins);
        self.discovered
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clear();
    }
    pub async fn load(db: &Db) -> anyhow::Result<Self> {
        const KEY: &str = "plex_installation";
        let stored = db
            .config()
            .await?
            .into_iter()
            .find(|(key, _)| key == KEY)
            .map(|(_, value)| value);
        let installation = if let Some(stored) = stored {
            serde_json::from_str::<Uuid>(&stored)?
        } else {
            let id = Uuid::new_v4();
            let value = serde_json::to_string(&id)?;
            db.write_config(&[(KEY, &value)], &[]).await?;
            id
        };
        Ok(Self {
            client: Client::new(installation)?,
            logins: Arc::default(),
            discovered: Mutex::default(),
        })
    }
    fn login(&self, handle: &str, owner: &str) -> Result<Arc<Login>, Fail> {
        self.logins
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(handle)
            .filter(|login| login.owner == owner)
            .cloned()
            .ok_or(Fail::NotFound)
    }
}
struct Reservation {
    logins: Logins,
    handle: String,
    keep: bool,
}
impl Drop for Reservation {
    fn drop(&mut self) {
        if !self.keep {
            self.logins
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .remove(&self.handle);
        }
    }
}
fn owner(headers: &HeaderMap) -> String {
    let mut hash = Sha256::new();
    if let Some(key) = headers.get("x-api-key") {
        hash.update(b"key");
        hash.update(key.as_bytes());
    } else if let Some(bearer) = headers
        .get("authorization")
        .and_then(|h| h.as_bytes().strip_prefix(b"Bearer "))
    {
        hash.update(b"key");
        hash.update(bearer);
    } else if let Some(cookie) = crate::auth::cookie(headers, crate::auth::COOKIE) {
        hash.update(b"session");
        hash.update(cookie.as_bytes());
    } else {
        hash.update(b"trusted administrator");
    }
    hash.finalize()
        .iter()
        .fold(String::new(), |mut text, byte| {
            write!(text, "{byte:02x}").expect("string");
            text
        })
}
async fn require_editable(admin: &Admin, path: &str) -> Result<(), Fail> {
    let locked = admin
        .config
        .read()
        .await
        .locked()
        .any(|managed| path == managed || path.starts_with(&format!("{managed}.")));
    if locked {
        return Err(Fail::Setting(path.to_owned(),"This Plex connection list is managed by the environment; update its environment setting instead".into()));
    }
    Ok(())
}
fn fail(error: infinidysk_plex::Error) -> Fail {
    Fail::Unavailable(error.to_string())
}
fn plex_generation(settings: &infinidysk_plex::Settings) -> Result<[u8; 32], Fail> {
    Ok(Sha256::digest(serde_json::to_vec(settings).map_err(|_| Fail::Internal)?).into())
}
fn credential(account: &Account) -> [u8; 32] {
    Sha256::digest(account.token.0.as_bytes()).into()
}
#[derive(Serialize, ToSchema)]
#[schema(as=PlexLoginStart)]
pub struct LoginStart {
    pub handle: String,
    pub url: String,
    pub expires_at_ms: i64,
}
#[derive(Serialize, ToSchema)]
#[schema(as=PlexLoginStatus)]
pub struct LoginStatus {
    pub state: String,
    pub expires_at_ms: i64,
}
#[utoipa::path(post,operation_id="plex_login_start",path="/api/v1/plex/login",responses((status=200,body=LoginStart)))]
pub async fn start(
    State(admin): State<Arc<Admin>>,
    headers: HeaderMap,
) -> Result<Json<LoginStart>, Fail> {
    require_editable(&admin, "plex.accounts").await?;
    Ok(Json(start_login(&admin.plex, owner(&headers)).await?))
}
async fn start_login(service: &Service, owner: String) -> Result<LoginStart, Fail> {
    let handle = Uuid::new_v4().to_string();
    let expires = now_ms() + 900_000;
    let login = Arc::new(Login {
        owner,
        expires: AtomicI64::new(expires),
        cancelled: AtomicBool::new(false),
        state: AsyncMutex::default(),
    });
    {
        let mut logins = service
            .logins
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        logins.retain(|_, entry| entry.expires.load(Ordering::Relaxed) > now_ms());
        if logins.len() >= 16 {
            return Err(Fail::Conflict(
                "Too many pending Plex connections; cancel one or wait for expiry".into(),
            ));
        }
        logins.insert(handle.clone(), Arc::clone(&login));
        drop(logins);
    }
    let mut reservation = Reservation {
        logins: Arc::clone(&service.logins),
        handle: handle.clone(),
        keep: false,
    };
    let pin = service.client.start_pin().await.map_err(fail)?;
    if pin.code.is_empty() || pin.code.len() > 128 {
        return Err(Fail::Unavailable(
            "Plex did not return a usable sign-in code".into(),
        ));
    }
    let expires = now_ms() + i64::try_from(pin.expires_in.clamp(1, 900)).expect("bounded") * 1000;
    login.expires.store(expires, Ordering::Relaxed);
    login.state.lock().await.pin = Some(pin.id);
    reservation.keep = true;
    drop(reservation);
    let encode = |text: &str| {
        percent_encoding::utf8_percent_encode(text, percent_encoding::NON_ALPHANUMERIC).to_string()
    };
    let url = format!(
        "https://app.plex.tv/auth#?clientID={}&code={}&context[device][product]=InfiniDysk",
        encode(service.client.installation()),
        encode(&pin.code)
    );
    Ok(LoginStart {
        handle,
        url,
        expires_at_ms: expires,
    })
}
#[utoipa::path(get,operation_id="plex_login_poll",path="/api/v1/plex/login/{handle}",params(("handle"=String,Path)),responses((status=200,body=LoginStatus)))]
pub async fn poll(
    State(admin): State<Arc<Admin>>,
    Path(handle): Path<String>,
    headers: HeaderMap,
) -> Result<Json<LoginStatus>, Fail> {
    Ok(Json(
        poll_login(&admin.plex, &handle, &owner(&headers), &admin.config).await?,
    ))
}
async fn poll_login(
    service: &Service,
    handle: &str,
    owner: &str,
    config: &tokio::sync::RwLock<crate::config::ConfigStore>,
) -> Result<LoginStatus, Fail> {
    let login = service.login(handle, owner)?;
    let mut state = login.state.lock().await;
    let expires = login.expires.load(Ordering::Relaxed);
    let status = if state.connected {
        "connected"
    } else if state.cancelled || login.cancelled.load(Ordering::SeqCst) {
        "cancelled"
    } else if expires <= now_ms() {
        "expired"
    } else if let Some(pin) = state.pin {
        let pin = service.client.poll_pin(pin).await.map_err(fail)?;
        if let Some(token) = pin.token {
            let account = service.client.account(&token).await.map_err(fail)?;
            if save_account(config, account, Some(&login), None).await? {
                state.connected = true;
                "connected"
            } else {
                state.cancelled = true;
                if expires <= now_ms() {
                    "expired"
                } else {
                    "cancelled"
                }
            }
        } else {
            "pending"
        }
    } else {
        "pending"
    };
    drop(state);
    Ok(LoginStatus {
        state: status.into(),
        expires_at_ms: expires,
    })
}
#[utoipa::path(delete,operation_id="plex_login_cancel",path="/api/v1/plex/login/{handle}",params(("handle"=String,Path)),responses((status=200,body=LoginStatus)))]
pub async fn cancel(
    State(admin): State<Arc<Admin>>,
    Path(handle): Path<String>,
    headers: HeaderMap,
) -> Result<Json<LoginStatus>, Fail> {
    let login = admin.plex.login(&handle, &owner(&headers))?;
    login.cancelled.store(true, Ordering::SeqCst);
    let mut state = login.state.lock().await;
    state.cancelled = true;
    let status = if state.connected {
        "connected"
    } else {
        "cancelled"
    };
    drop(state);
    Ok(Json(LoginStatus {
        state: status.into(),
        expires_at_ms: login.expires.load(Ordering::Relaxed),
    }))
}
async fn save_account(
    config: &tokio::sync::RwLock<crate::config::ConfigStore>,
    account: Account,
    login: Option<&Login>,
    parent: Option<&Account>,
) -> Result<bool, Fail> {
    let mut store = config.write().await;
    if login.is_some_and(|login| {
        login.cancelled.load(Ordering::SeqCst) || login.expires.load(Ordering::Relaxed) <= now_ms()
    }) {
        return Ok(false);
    }
    let mut next = store.settings().clone();
    if let Some(parent) = parent
        && next
            .plex
            .accounts
            .iter()
            .find(|a| a.id == parent.id)
            .map(credential)
            != Some(credential(parent))
    {
        return Err(Fail::Conflict(
            "The parent Plex connection changed; retry profile selection".into(),
        ));
    }
    next.plex.accounts.retain(|current| {
        current.id != account.id && account.numeric_id.as_deref() != Some(current.id.as_str())
    });
    next.plex.accounts.push(account);
    store.save(next).await?;
    drop(store);
    Ok(true)
}
async fn account(admin: &Admin, id: &str) -> Result<Account, Fail> {
    admin
        .config
        .read()
        .await
        .settings()
        .plex
        .accounts
        .iter()
        .find(|a| a.id == id)
        .cloned()
        .ok_or(Fail::NotFound)
}
async fn verified_account(admin: &Admin, id: &str) -> Result<Account, Fail> {
    let saved = account(admin, id).await?;
    let actual = admin
        .plex
        .client
        .account(&saved.token)
        .await
        .map_err(fail)?;
    if actual.id != saved.id && actual.numeric_id.as_deref() != Some(saved.id.as_str()) {
        return Err(Fail::Conflict(
            "The saved Plex credential belongs to another profile; reconnect it".into(),
        ));
    }
    Ok(saved)
}
#[derive(Serialize, ToSchema)]
#[schema(as=PlexDiscoveredServer)]
pub struct ServerHandle {
    pub handle: String,
    pub id: String,
    pub name: String,
    pub connections: Vec<Connection>,
    pub owned: bool,
}
#[utoipa::path(post,operation_id="plex_discover_servers",path="/api/v1/plex/accounts/{id}/discover",params(("id"=String,Path)),responses((status=200,body=Vec<ServerHandle>)))]
pub async fn discover(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
    headers: HeaderMap,
) -> Result<Json<Vec<ServerHandle>>, Fail> {
    let account = account(&admin, &id).await?;
    let actual = admin
        .plex
        .client
        .account(&account.token)
        .await
        .map_err(fail)?;
    if actual.id != account.id && actual.numeric_id.as_deref() != Some(account.id.as_str()) {
        return Err(Fail::Conflict(
            "The saved Plex credential belongs to another profile; reconnect it".into(),
        ));
    }
    let resources = admin
        .plex
        .client
        .discover(&account.token)
        .await
        .map_err(fail)?;
    let owner = owner(&headers);
    let expires = now_ms() + 300_000;
    let mut discovered = admin
        .plex
        .discovered
        .lock()
        .unwrap_or_else(PoisonError::into_inner);
    discovered.retain(|_, entry| entry.expires > now_ms());
    if discovered.len() + resources.len() > 256 {
        return Err(Fail::Conflict(
            "Too many pending Plex server selections; wait for expiry".into(),
        ));
    }
    let mut output = Vec::new();
    for resource in resources {
        let handle = Uuid::new_v4().to_string();
        output.push(ServerHandle {
            handle: handle.clone(),
            id: resource.id.clone(),
            name: resource.name.clone(),
            connections: resource.connections.clone(),
            owned: resource.owned,
        });
        discovered.insert(
            handle,
            Discovered {
                owner: owner.clone(),
                account: id.clone(),
                credential: credential(&account),
                resource,
                expires,
            },
        );
    }
    drop(discovered);
    Ok(Json(output))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PlexSelectServer)]
pub struct Selection {
    pub handle: String,
    pub url: String,
}
#[utoipa::path(post,operation_id="plex_select_server",path="/api/v1/plex/servers",request_body=Selection,responses((status=200,body=String)))]
pub async fn select(
    State(admin): State<Arc<Admin>>,
    headers: HeaderMap,
    Json(selection): Json<Selection>,
) -> Result<Json<String>, Fail> {
    require_editable(&admin, "plex.servers").await?;
    let (account_id, expected, resource) = {
        let discovered = admin
            .plex
            .discovered
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let entry = discovered
            .get(&selection.handle)
            .filter(|entry| entry.expires > now_ms() && entry.owner == owner(&headers))
            .ok_or(Fail::NotFound)?;
        if !entry
            .resource
            .connections
            .iter()
            .any(|c| c.uri == selection.url)
        {
            return Err(Fail::Bad(
                "Select one of the discovered Plex endpoints".into(),
            ));
        }
        let snapshot = (
            entry.account.clone(),
            entry.credential,
            entry.resource.clone(),
        );
        drop(discovered);
        snapshot
    };
    if credential(&account(&admin, &account_id).await?) != expected {
        return Err(Fail::Conflict(
            "Plex credentials changed; discover servers again".into(),
        ));
    }
    let server = Server {
        id: resource.id.clone(),
        name: resource.name,
        url: selection.url,
        token: resource.token,
        enabled: true,
        account_id: Some(account_id),
        path_mappings: vec![],
    };
    let generation = {
        let config = admin.config.read().await;
        plex_generation(&config.settings().plex)?
    };
    admin.plex.client.test_server(&server).await.map_err(fail)?;
    let mut store = admin.config.write().await;
    let mut next = store.settings().clone();
    if plex_generation(&next.plex)? != generation {
        return Err(Fail::Conflict(
            "Plex settings changed; select the endpoint again".into(),
        ));
    }
    if next
        .plex
        .accounts
        .iter()
        .find(|a| a.id == server.account_id.as_deref().unwrap_or_default())
        .map(credential)
        != Some(expected)
    {
        return Err(Fail::Conflict(
            "Plex credentials changed; discover servers again".into(),
        ));
    }
    if let Some(existing) = next.plex.servers.iter_mut().find(|s| s.id == server.id) {
        existing.url.clone_from(&server.url);
        existing.token.clone_from(&server.token);
        existing.account_id.clone_from(&server.account_id);
    } else {
        next.plex.servers.push(server);
    }
    store.save(next).await?;
    drop(store);
    Ok(Json(resource.id))
}
#[utoipa::path(get,operation_id="plex_home_users",path="/api/v1/plex/accounts/{id}/home",params(("id"=String,Path)),responses((status=200,body=Vec<HomeUser>)))]
pub async fn home(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
) -> Result<Json<Vec<HomeUser>>, Fail> {
    let account = verified_account(&admin, &id).await?;
    Ok(Json(
        admin
            .plex
            .client
            .home_users(&account.token)
            .await
            .map_err(fail)?,
    ))
}
#[derive(Deserialize, ToSchema)]
#[schema(as=PlexSwitchHome)]
pub struct Switch {
    pub user_id: String,
    pub pin: Option<Secret>,
}
#[utoipa::path(post,operation_id="plex_switch_home",path="/api/v1/plex/accounts/{id}/home",params(("id"=String,Path)),request_body=Switch,responses((status=200,body=String)))]
pub async fn switch_home(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
    Json(request): Json<Switch>,
) -> Result<Json<String>, Fail> {
    require_editable(&admin, "plex.accounts").await?;
    let account = verified_account(&admin, &id).await?;
    let profile = admin
        .plex
        .client
        .switch_home(
            &account.token,
            &request.user_id,
            request.pin.as_ref().map(|s| s.0.as_str()),
        )
        .await
        .map_err(fail)?;
    let id = profile.id.clone();
    save_account(&admin.config, profile, None, Some(&account)).await?;
    Ok(Json(id))
}
#[utoipa::path(get,operation_id="plex_libraries",path="/api/v1/plex/servers/{id}/libraries",params(("id"=String,Path)),responses((status=200,body=Vec<Library>)))]
pub async fn libraries(
    State(admin): State<Arc<Admin>>,
    Path(id): Path<String>,
) -> Result<Json<Vec<Library>>, Fail> {
    let server = admin
        .config
        .read()
        .await
        .settings()
        .plex
        .servers
        .iter()
        .find(|s| s.id == id)
        .cloned()
        .ok_or(Fail::NotFound)?;
    admin.plex.client.test_server(&server).await.map_err(fail)?;
    Ok(Json(
        admin.plex.client.libraries(&server).await.map_err(fail)?,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::ConfigStore;
    use axum::{
        Router,
        body::Body,
        extract::State as AxumState,
        http::{Method, Response, Uri},
    };
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, AtomicUsize};
    #[derive(Default)]
    struct Cloud {
        ready: AtomicBool,
        fail: AtomicBool,
        starts: AtomicUsize,
        polls: AtomicUsize,
    }
    struct Fixture {
        service: Service,
        config: tokio::sync::RwLock<ConfigStore>,
        cloud: Arc<Cloud>,
        task: tokio::task::JoinHandle<()>,
        root: PathBuf,
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            self.task.abort();
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }
    async fn fixture() -> Fixture {
        async fn request(
            AxumState(cloud): AxumState<Arc<Cloud>>,
            method: Method,
            uri: Uri,
        ) -> Response<Body> {
            if cloud.fail.load(Ordering::SeqCst) {
                return Response::builder()
                    .status(503)
                    .body(Body::empty())
                    .unwrap_or_else(|_| panic!("response"));
            }
            let body = match (method, uri.path()) {
                (Method::POST, "/api/v2/pins") => {
                    serde_json::json!({"id":cloud.starts.fetch_add(1,Ordering::SeqCst)+1,"code":"fixture-code","expiresIn":900})
                }
                (Method::GET, path) if path.starts_with("/api/v2/pins/") => {
                    cloud.polls.fetch_add(1, Ordering::SeqCst);
                    serde_json::json!({"id":1,"authToken":if cloud.ready.load(Ordering::SeqCst){Some("fixture-account-token")}else{None}})
                }
                (Method::GET, "/api/v2/user") => {
                    serde_json::json!({"uuid":"fixture-account","username":"fixture","id":42})
                }
                _ => {
                    return Response::builder()
                        .status(404)
                        .body(Body::empty())
                        .unwrap_or_else(|_| panic!("response"));
                }
            };
            Response::builder()
                .status(200)
                .body(Body::from(body.to_string()))
                .unwrap_or_else(|_| panic!("response"))
        }
        let cloud = Arc::new(Cloud::default());
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0")
            .await
            .unwrap_or_else(|_| panic!("listener"));
        let port = listener
            .local_addr()
            .unwrap_or_else(|_| panic!("address"))
            .port();
        let router = Router::new()
            .fallback(request)
            .with_state(Arc::clone(&cloud));
        let task = tokio::spawn(async move {
            let _ = axum::serve(listener, router).await;
        });
        let root = std::env::temp_dir().join(format!("id-plex-service-{}", Uuid::new_v4()));
        std::fs::create_dir_all(&root).unwrap_or_else(|_| panic!("directory"));
        let db = Db::connect(&format!("sqlite:{}", root.join("db.sqlite").display()))
            .await
            .unwrap_or_else(|_| panic!("database"));
        let mut config = ConfigStore::load(db, [])
            .await
            .unwrap_or_else(|_| panic!("config"));
        config
            .generate_missing_secrets()
            .await
            .unwrap_or_else(|_| panic!("keys"));
        let service = Service {
            client: Client::loopback(Uuid::new_v4(), port)
                .unwrap_or_else(|_| panic!("local client")),
            logins: Arc::default(),
            discovered: Mutex::default(),
        };
        Fixture {
            service,
            config: tokio::sync::RwLock::new(config),
            cloud,
            task,
            root,
        }
    }
    #[tokio::test]
    async fn sign_in_handles_are_owner_bound_and_the_token_never_reaches_the_response() {
        let f = fixture().await;
        let start = start_login(&f.service, "owner-a".into())
            .await
            .unwrap_or_else(|_| panic!("start"));
        assert!(start.url.starts_with("https://app.plex.tv/auth#?clientID="));
        assert!(matches!(
            poll_login(&f.service, &start.handle, "owner-b", &f.config).await,
            Err(Fail::NotFound)
        ));
        assert_eq!(f.cloud.polls.load(Ordering::SeqCst), 0);
        assert_eq!(
            poll_login(&f.service, &start.handle, "owner-a", &f.config)
                .await
                .unwrap_or_else(|_| panic!("pending"))
                .state,
            "pending"
        );
        f.cloud.ready.store(true, Ordering::SeqCst);
        let connected = poll_login(&f.service, &start.handle, "owner-a", &f.config)
            .await
            .unwrap_or_else(|_| panic!("connect"));
        assert_eq!(connected.state, "connected");
        assert!(
            !serde_json::to_string(&connected)
                .unwrap_or_else(|_| panic!("JSON"))
                .contains("fixture-account-token")
        );
        let store = f.config.read().await;
        assert_eq!(store.settings().plex.accounts.len(), 1);
        assert_eq!(store.settings().plex.accounts[0].id, "fixture-account");
        assert!(
            !serde_json::to_string(&store.settings().masked())
                .unwrap_or_else(|_| panic!("masked JSON"))
                .contains("fixture-account-token")
        );
        drop(store);
        assert_eq!(
            poll_login(&f.service, &start.handle, "owner-a", &f.config)
                .await
                .unwrap_or_else(|_| panic!("repeat"))
                .state,
            "connected"
        );
        assert_eq!(f.cloud.polls.load(Ordering::SeqCst), 2);
    }
    #[tokio::test]
    async fn cancelled_expired_and_failed_reservations_do_not_store_credentials() {
        let f = fixture().await;
        f.cloud.fail.store(true, Ordering::SeqCst);
        assert!(start_login(&f.service, "owner".into()).await.is_err());
        assert_eq!(
            f.service
                .logins
                .lock()
                .unwrap_or_else(|_| panic!("logins"))
                .len(),
            0
        );
        f.cloud.fail.store(false, Ordering::SeqCst);
        let start = start_login(&f.service, "owner".into())
            .await
            .unwrap_or_else(|_| panic!("start"));
        let login = f
            .service
            .login(&start.handle, "owner")
            .unwrap_or_else(|_| panic!("handle"));
        login.expires.store(now_ms() - 1, Ordering::Relaxed);
        assert_eq!(
            poll_login(&f.service, &start.handle, "owner", &f.config)
                .await
                .unwrap_or_else(|_| panic!("expired"))
                .state,
            "expired"
        );
        let start = start_login(&f.service, "owner".into())
            .await
            .unwrap_or_else(|_| panic!("next start"));
        f.cloud.ready.store(true, Ordering::SeqCst);
        let login = f
            .service
            .login(&start.handle, "owner")
            .unwrap_or_else(|_| panic!("handle"));
        login.cancelled.store(true, Ordering::SeqCst);
        assert_eq!(
            poll_login(&f.service, &start.handle, "owner", &f.config)
                .await
                .unwrap_or_else(|_| panic!("cancelled"))
                .state,
            "cancelled"
        );
        assert_eq!(f.config.read().await.settings().plex.accounts, []);
        f.service.invalidate_pending();
        assert!(matches!(
            poll_login(&f.service, &start.handle, "owner", &f.config).await,
            Err(Fail::NotFound)
        ));
    }
    #[tokio::test]
    async fn pending_sign_in_capacity_is_enforced_before_contacting_plex() {
        let f = fixture().await;
        for _ in 0..16 {
            start_login(&f.service, "owner".into())
                .await
                .unwrap_or_else(|_| panic!("bounded start"));
        }
        assert!(matches!(
            start_login(&f.service, "owner".into()).await,
            Err(Fail::Conflict(_))
        ));
        assert_eq!(f.cloud.starts.load(Ordering::SeqCst), 16);
    }
    #[test]
    fn connection_owner_ignores_unrelated_cookies_and_uses_the_auth_credential() {
        let mut first = HeaderMap::new();
        first.insert(
            "cookie",
            "infinidysk_session=fixture; theme=dark"
                .parse()
                .unwrap_or_else(|_| panic!("header")),
        );
        let mut second = HeaderMap::new();
        second.insert(
            "cookie",
            "theme=light; infinidysk_session=fixture"
                .parse()
                .unwrap_or_else(|_| panic!("header")),
        );
        assert_eq!(owner(&first), owner(&second));
        second.insert(
            "x-api-key",
            "fixture-key".parse().unwrap_or_else(|_| panic!("header")),
        );
        assert_ne!(owner(&first), owner(&second));
        first.insert(
            "authorization",
            "Bearer fixture-key"
                .parse()
                .unwrap_or_else(|_| panic!("header")),
        );
        assert_eq!(owner(&first), owner(&second));
    }
    #[tokio::test]
    async fn account_storage_preserves_managed_lists_and_normalizes_legacy_numeric_ids() {
        let f = fixture().await;
        let db = Db::connect(&format!("sqlite:{}", f.root.join("db.sqlite").display()))
            .await
            .unwrap_or_else(|_| panic!("database"));
        let managed = ConfigStore::load(db, [("INFINIDYSK__PLEX__ACCOUNTS".into(), "[]".into())])
            .await
            .unwrap_or_else(|_| panic!("managed config"));
        let managed = tokio::sync::RwLock::new(managed);
        let canonical = Account {
            id: "canonical".into(),
            name: "fixture".into(),
            token: infinidysk_plex::Token("fixture-token".into()),
            numeric_id: Some("42".into()),
        };
        assert!(matches!(
            save_account(&managed, canonical.clone(), None, None).await,
            Err(Fail::Setting(_, _))
        ));
        let mut store = f.config.write().await;
        let mut next = store.settings().clone();
        next.plex.accounts = vec![Account {
            id: "42".into(),
            ..canonical.clone()
        }];
        store
            .save(next)
            .await
            .unwrap_or_else(|_| panic!("legacy account"));
        drop(store);
        save_account(&f.config, canonical, None, None)
            .await
            .unwrap_or_else(|_| panic!("normalize"));
        let store = f.config.read().await;
        assert_eq!(store.settings().plex.accounts.len(), 1);
        assert_eq!(store.settings().plex.accounts[0].id, "canonical");
    }
    #[tokio::test]
    async fn changing_a_parent_credential_stops_an_in_flight_home_profile_save() {
        let f = fixture().await;
        let parent = Account {
            id: "parent".into(),
            name: "fixture".into(),
            token: infinidysk_plex::Token("fixture-old-token".into()),
            numeric_id: None,
        };
        let mut store = f.config.write().await;
        let mut next = store.settings().clone();
        next.plex.accounts = vec![Account {
            token: infinidysk_plex::Token("fixture-new-token".into()),
            ..parent.clone()
        }];
        store
            .save(next)
            .await
            .unwrap_or_else(|_| panic!("changed credential"));
        drop(store);
        let child = Account {
            id: "child".into(),
            ..parent.clone()
        };
        assert!(matches!(
            save_account(&f.config, child, None, Some(&parent)).await,
            Err(Fail::Conflict(_))
        ));
        let store = f.config.read().await;
        assert_eq!(store.settings().plex.accounts.len(), 1);
        assert_eq!(store.settings().plex.accounts[0].id, "parent");
    }
}
