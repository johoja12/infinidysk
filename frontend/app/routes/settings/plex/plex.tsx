import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Button, Input, ManagedSetting, Select, SettingsCard } from "~/components/ui";
import {
  loadPlexBootstrap,
  plexRequest,
  publishPlexServers,
  serverSaveRequest,
  validServer,
  type PlexAccount,
  type PlexCandidate,
  type PlexHomeUser,
  type PlexLogin,
  type PlexServer,
} from "./plex-api";
import { PlexServerEditor } from "./plex-server-editor";

export function PlexSettings() {
  const [accounts, setAccounts] = useState<PlexAccount[]>([]);
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [savedIds, setSavedIds] = useState<string[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [login, setLogin] = useState<PlexLogin | null>(null);
  const [state, setState] = useState("idle");
  const [homeUsers, setHomeUsers] = useState<PlexHomeUser[]>([]);
  const [homeId, setHomeId] = useState("");
  const [pin, setPin] = useState("");
  const [candidates, setCandidates] = useState<PlexCandidate[]>([]);
  const [connections, setConnections] = useState<Record<string, string>>({});
  const polling = useRef(false);
  const loginGeneration = useRef(0);
  const refreshAccounts = async () =>
    setAccounts((await plexRequest<{ accounts: PlexAccount[] }>("accounts")).accounts);
  const refreshServers = async () => {
    const result = (await plexRequest<{ servers: PlexServer[] }>("servers")).servers;
    setServers(result);
    setSavedIds(result.map((server) => server.id));
    publishPlexServers(result);
  };
  useEffect(() => {
    let alive = true;
    const generation = loginGeneration;
    void loadPlexBootstrap()
      .then((result) => {
        if (alive) {
          setAccounts(result.accounts);
          setServers(result.servers);
          setSavedIds(result.servers.map((server) => server.id));
          setLoaded(true);
        }
      })
      .catch((cause) => {
        if (alive)
          setError(cause instanceof Error ? cause.message : "Plex settings could not be loaded.");
      });
    return () => {
      alive = false;
      generation.current++;
    };
  }, []);
  const act = async (operation: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    setMessage("");
    try {
      await operation();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Plex operation failed.");
    } finally {
      setBusy(false);
    }
  };
  const checkLogin = useCallback(async () => {
    if (!login || state !== "pending" || polling.current) return;
    if (Date.now() >= Date.parse(login.expiresAt)) {
      setState("expired");
      return;
    }
    polling.current = true;
    const generation = loginGeneration.current;
    try {
      const result = await plexRequest<{ state: string }>("login/poll", { handle: login.handle });
      if (generation !== loginGeneration.current) return;
      setState(result.state);
      if (result.state === "connected") {
        const saved = await plexRequest<{ accounts: PlexAccount[] }>("accounts");
        if (generation !== loginGeneration.current) return;
        setAccounts(saved.accounts);
        setMessage("Plex account connected and saved.");
      }
    } catch (cause) {
      if (generation === loginGeneration.current) {
        setError(cause instanceof Error ? cause.message : "Sign-in could not be checked.");
        setState("failed");
      }
    } finally {
      polling.current = false;
    }
  }, [login, state]);
  useEffect(() => {
    if (state !== "pending") return;
    const timer = setInterval(() => void checkLogin(), 2000);
    return () => clearInterval(timer);
  }, [state, checkLogin]);
  const start = () =>
    act(async () => {
      loginGeneration.current++;
      if (login) await plexRequest("login/cancel", { handle: login.handle });
      setHomeUsers([]);
      setCandidates([]);
      setPin("");
      setLogin(await plexRequest<PlexLogin>("login/start"));
      setState("pending");
    });
  const select = (accountId: string) =>
    act(async () => {
      loginGeneration.current++;
      const selected = await plexRequest<PlexLogin>("account/select", { accountId });
      setLogin(selected);
      setState("connected");
      setHomeUsers([]);
      setCandidates([]);
      setPin("");
    });
  const save = () =>
    act(async () => {
      const result = await plexRequest<{ servers: PlexServer[] }>("save", {
        servers: servers.map(serverSaveRequest),
      });
      setServers(result.servers);
      setSavedIds(result.servers.map((server) => server.id));
      publishPlexServers(result.servers);
      setMessage("Plex servers saved. General Apply is not required.");
    });
  return (
    <div className="space-y-5">
      {error && <Alert variant="danger">{error}</Alert>}
      {message && <p role="status">{message}</p>}
      <SettingsCard
        icon="account_circle"
        title="Plex accounts"
        description="Optional advanced integration. Credentials stay on the server; this does not change your Plex libraries or enable warming."
      >
        {!loaded ? (
          <p>Loading Plex settings…</p>
        ) : (
          <>
            <div className="my-3 flex flex-wrap gap-2">
              {accounts.map((account) => (
                <Button
                  key={account.id}
                  type="button"
                  disabled={busy}
                  onClick={() => void select(account.id)}
                >
                  Use account {account.name}
                </Button>
              ))}
            </div>
            <ManagedSetting configKey="plex.accounts">
              <Button type="button" disabled={busy} onClick={() => void start()}>
                Sign in with Plex
              </Button>
              <p className="mt-2 text-xs">
                Account changes save immediately, separately from General Apply.
              </p>
              {accounts.map((account) => (
                <div key={account.id} className="my-3 flex flex-wrap items-center gap-2">
                  <span>{account.name}</span>
                  <Button type="button" disabled={busy} onClick={() => void start()}>
                    Reconnect account {account.name}
                  </Button>
                  <Button
                    type="button"
                    disabled={busy}
                    onClick={() => {
                      if (
                        !globalThis.confirm(
                          "Remove this saved account and disable its linked servers? Independent manually configured servers and cached files are retained. This does not revoke authorization at Plex. Unsaved server edits will reload.",
                        )
                      )
                        return;
                      void act(async () => {
                        loginGeneration.current++;
                        await plexRequest("account/disconnect", { accountId: account.id });
                        setLogin(null);
                        setState("idle");
                        setCandidates([]);
                        setHomeUsers([]);
                        await refreshAccounts();
                        await refreshServers();
                      });
                    }}
                  >
                    Disconnect account {account.name}
                  </Button>
                </div>
              ))}
              {state === "pending" && login && (
                <div className="my-3 space-y-2">
                  <p>
                    Waiting for Plex authorization. Expires{" "}
                    {new Date(login.expiresAt).toLocaleTimeString()}.
                  </p>
                  <a className="link" href={login.url} target="_blank" rel="noreferrer noopener">
                    Open Plex sign-in
                  </a>
                  <div className="flex gap-2">
                    <Button type="button" disabled={busy} onClick={() => void checkLogin()}>
                      Check sign-in
                    </Button>
                    <Button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void act(async () => {
                          loginGeneration.current++;
                          await plexRequest("login/cancel", { handle: login.handle });
                          setState("cancelled");
                          setLogin(null);
                        })
                      }
                    >
                      Cancel sign-in
                    </Button>
                  </div>
                </div>
              )}
              {state === "expired" && (
                <p role="status">Sign-in expired. Sign in with Plex again to retry.</p>
              )}
              {state === "cancelled" && <p role="status">Sign-in cancelled.</p>}
              {state === "failed" && (
                <p role="status">Sign-in failed. Sign in with Plex again to retry.</p>
              )}
              {state === "connected" && login && (
                <div className="space-y-3">
                  <p>Account ready for server discovery.</p>
                  <Button
                    type="button"
                    disabled={busy}
                    onClick={() =>
                      void act(async () => {
                        setHomeUsers(
                          (
                            await plexRequest<{ users: PlexHomeUser[] }>("home/users", {
                              handle: login.handle,
                            })
                          ).users,
                        );
                        setHomeId("");
                      })
                    }
                  >
                    Load Plex Home users
                  </Button>
                  {homeUsers.length > 0 && (
                    <>
                      <label>
                        Plex Home user
                        <Select
                          aria-label="Plex Home user"
                          value={homeId}
                          onChange={(event) => setHomeId(event.target.value)}
                        >
                          <option value="">Choose user</option>
                          {homeUsers.map((user) => (
                            <option key={user.id} value={user.id}>
                              {user.name}
                              {user.protected ? " (PIN protected)" : ""}
                            </option>
                          ))}
                        </Select>
                      </label>
                      {homeUsers.find((user) => user.id === homeId)?.protected && (
                        <label>
                          Plex Home PIN
                          <Input
                            aria-label="Plex Home PIN"
                            type="password"
                            inputMode="numeric"
                            autoComplete="off"
                            maxLength={4}
                            value={pin}
                            onChange={(event) => setPin(event.target.value)}
                          />
                        </label>
                      )}
                      <Button
                        type="button"
                        disabled={
                          busy ||
                          !homeId ||
                          Boolean(
                            homeUsers.find((user) => user.id === homeId)?.protected &&
                            !/^\d{4}$/.test(pin),
                          )
                        }
                        onClick={() =>
                          void act(async () => {
                            try {
                              const switched = await plexRequest<{
                                handle: string;
                                expiresAt: string;
                              }>("home/switch", { handle: login.handle, userId: homeId, pin });
                              setLogin({ ...switched, url: "" });
                              setCandidates([]);
                              setHomeUsers([]);
                              await refreshAccounts();
                              setMessage("Plex Home user connected and saved.");
                            } finally {
                              setPin("");
                            }
                          })
                        }
                      >
                        Switch Plex Home user
                      </Button>
                    </>
                  )}
                </div>
              )}
            </ManagedSetting>
            <p className="text-xs text-base-content/60">
              Disconnect removes local account credentials and disables linked servers. Independent
              manually token-configured servers and cached media remain. Revoke remote authorization
              separately in Plex.
            </p>
          </>
        )}
      </SettingsCard>
      {loaded && (
        <SettingsCard
          icon="dns"
          title="Plex servers and mappings"
          description="Choose a tested server connection. Save here applies immediately without overwriting unrelated settings."
        >
          <ManagedSetting configKey="plex.servers">
            {state === "connected" && login && (
              <Button
                type="button"
                disabled={busy}
                onClick={() =>
                  void act(async () => {
                    setCandidates(
                      (
                        await plexRequest<{ servers: PlexCandidate[] }>("discover", {
                          handle: login.handle,
                        })
                      ).servers,
                    );
                  })
                }
              >
                Discover Plex servers
              </Button>
            )}
            {candidates.map((candidate) => (
              <div
                key={candidate.handle}
                className="my-3 space-y-2 rounded border border-base-content/10 p-3"
              >
                <p>
                  {candidate.name} ({candidate.id})
                </p>
                <label>
                  Connection for {candidate.name}
                  <Select
                    aria-label={`Connection for ${candidate.name}`}
                    value={connections[candidate.handle] ?? candidate.connections[0]?.uri ?? ""}
                    onChange={(event) =>
                      setConnections((current) => ({
                        ...current,
                        [candidate.handle]: event.target.value,
                      }))
                    }
                  >
                    {candidate.connections.map((connection) => (
                      <option key={connection.uri} value={connection.uri}>
                        {connection.uri} (
                        {connection.relay ? "relay" : connection.local ? "local" : "remote"})
                      </option>
                    ))}
                  </Select>
                </label>
                <Button
                  type="button"
                  disabled={busy || candidate.connections.length === 0}
                  onClick={() => {
                    const server: PlexServer = {
                      id: candidate.id,
                      name: candidate.name,
                      url: connections[candidate.handle] ?? candidate.connections[0]!.uri,
                      token: "",
                      enabled: true,
                      pathMappings:
                        servers.find((item) => item.id === candidate.id)?.pathMappings ?? [],
                      handle: candidate.handle,
                    };
                    setServers((current) =>
                      current.some((item) => item.id === candidate.id)
                        ? current.map((item) => (item.id === candidate.id ? server : item))
                        : [...current, server],
                    );
                  }}
                >
                  Choose {candidate.name}
                </Button>
              </div>
            ))}
            <div className="my-3 space-y-3">
              {servers.map((server, index) => (
                <PlexServerEditor
                  key={index}
                  server={server}
                  index={index}
                  busy={busy}
                  onChange={(next) =>
                    setServers((current) =>
                      current.map((item, position) => (position === index ? next : item)),
                    )
                  }
                  onTest={() =>
                    void act(async () => {
                      await plexRequest("test", { server: serverSaveRequest(server) });
                      setMessage(`Server ${index + 1} identity and authorization verified.`);
                    })
                  }
                  onRemove={() =>
                    void act(async () => {
                      if (savedIds.includes(server.id)) {
                        if (
                          !globalThis.confirm("Remove this saved server? Cached media is retained.")
                        )
                          return;
                        await plexRequest("disconnect", { serverId: server.id });
                        setSavedIds((current) => current.filter((id) => id !== server.id));
                      }
                      setServers((current) => current.filter((_, position) => position !== index));
                    })
                  }
                />
              ))}
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                type="button"
                disabled={busy || servers.length >= 32}
                onClick={() =>
                  setServers((current) => [
                    ...current,
                    { id: "", name: "", url: "", token: "", enabled: true, pathMappings: [] },
                  ])
                }
              >
                Add manual server
              </Button>
              <Button
                type="button"
                disabled={busy || servers.some((server) => !validServer(server))}
                onClick={() => void save()}
              >
                Save Plex servers
              </Button>
            </div>
          </ManagedSetting>
        </SettingsCard>
      )}
    </div>
  );
}
