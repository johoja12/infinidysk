import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings, notAPassword } from "../settings";
import { Failure, Field, Loading, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";

type Plex = Schemas["PlexSettings"];
type Server = Schemas["PlexServer"];
const serverDefaults = (server: Server) => ({
  ...server,
  enabled: server.enabled ?? true,
  path_mappings: server.path_mappings ?? [],
});
type Account = Schemas["PlexAccount"];
type ServerHandle = Schemas["PlexDiscoveredServer"];
type Update = (change: (current: Plex) => Plex) => Promise<void>;

export function PlexConnections() {
  const view = useSettings();
  const save = useSaveSettings();
  const client = useQueryClient();
  const [login, setLogin] = useState<Schemas["PlexLoginStart"] | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [discovered, setDiscovered] = useState<{
    account: string;
    servers: ServerHandle[];
  } | null>(null);
  const [remove, setRemove] = useState<{
    kind: "account" | "server";
    id: string;
  } | null>(null);
  const locked = (path: string) =>
    view.data?.locked.some(
      (key) => key === path || path.startsWith(key + "."),
    ) ?? false;
  const start = useMutation({
    mutationFn: () => body(api.POST("/api/v1/plex/login")),
    onSuccess: (value) => {
      setNotice(null);
      setLogin(value);
    },
  });
  const status = useQuery({
    queryKey: ["plex-login", login?.handle],
    enabled: !!login,
    retry: false,
    queryFn: () =>
      body(
        api.GET("/api/v1/plex/login/{handle}", {
          params: { path: { handle: login!.handle } },
        }),
      ),
    refetchInterval: (query) =>
      query.state.data?.state === "pending" && !query.state.error
        ? 2000
        : false,
  });
  useEffect(() => {
    if (status.data && status.data.state !== "pending") {
      setNotice(
        status.data.state === "connected"
          ? "Plex account connected."
          : status.data.state === "expired"
            ? "Sign-in expired. Connect again."
            : "Connection cancelled.",
      );
      setLogin(null);
      void client.invalidateQueries({ queryKey: ["settings"] });
    }
  }, [status.data, client]);
  const cancel = useMutation({
    mutationFn: async () => {
      if (login)
        await body(
          api.DELETE("/api/v1/plex/login/{handle}", {
            params: { path: { handle: login.handle } },
          }),
        );
    },
    onSettled: () => {
      setLogin(null);
      void client.invalidateQueries({ queryKey: ["settings"] });
    },
  });
  const discover = useMutation({
    mutationFn: async (account: string) => ({
      account,
      servers: await body(
        api.POST("/api/v1/plex/accounts/{id}/discover", {
          params: { path: { id: account } },
        }),
      ),
    }),
    onSuccess: setDiscovered,
  });
  const select = useMutation({
    mutationFn: (request: Schemas["PlexSelectServer"]) =>
      body(api.POST("/api/v1/plex/servers", { body: request })),
    onSuccess: () => void client.invalidateQueries({ queryKey: ["settings"] }),
  });
  const update: Update = async (change) => {
    const latest = await body(api.GET("/api/v1/settings"));
    await save.mutateAsync({
      ...latest.settings,
      plex: change(latest.settings.plex),
    });
  };
  if (view.isPending) return <Loading />;
  if (view.error) return <Failure error={view.error} />;
  const config = view.data!.settings.plex;
  return (
    <div className="space-y-6">
      <AlertDialog
        open={!!remove}
        onOpenChange={(open) => {
          if (!open) setRemove(null);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              Disconnect this Plex connection?
            </AlertDialogTitle>
            <AlertDialogDescription>
              Stop using this connection for future Plex refreshes.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (remove) {
                  const target = remove;
                  void update((current) => ({
                    ...current,
                    ...(target.kind === "account"
                      ? {
                          accounts: current.accounts.filter(
                            (account) => account.id !== target.id,
                          ),
                        }
                      : {
                          servers: current.servers.filter(
                            (server) => server.id !== target.id,
                          ),
                        }),
                  })).catch(() => {});
                }
                setRemove(null);
              }}
            >
              Disconnect
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <SettingsPanel title="Plex accounts">
        <p className="text-sm text-muted-foreground">
          Connect the profiles used to watch your library. Watched-state access
          is verified for each viewer. Sign in with Plex, then return here.
        </p>
        {locked("plex.accounts") && (
          <p className="text-sm text-muted-foreground">
            Accounts are managed by the environment.
          </p>
        )}
        {[
          view.error,
          start.error,
          status.error,
          cancel.error,
          discover.error,
          select.error,
          save.error,
        ]
          .filter((error): error is Error => error !== null)
          .map((error, index) => (
            <Failure key={index} error={error} />
          ))}
        {notice && (
          <p className="text-sm" role="status">
            {notice}
          </p>
        )}
        <Button
          type="button"
          disabled={locked("plex.accounts") || start.isPending || !!login}
          onClick={() => start.mutate()}
        >
          Connect Plex account
        </Button>
        {login && (
          <div className="space-y-2 rounded-lg border p-3">
            <a
              className="text-sm text-primary underline"
              href={login.url}
              target="_blank"
              rel="noopener noreferrer"
            >
              Open Plex sign-in
            </a>
            <p className="text-sm text-muted-foreground">
              {status.error
                ? "Connection check failed. Cancel and try again."
                : "Waiting for Plex approval"}{" "}
              · expires {new Date(login.expires_at_ms).toLocaleTimeString()}
            </p>
            <Button
              type="button"
              variant="outline"
              disabled={cancel.isPending}
              onClick={() => cancel.mutate()}
            >
              Cancel connection
            </Button>
          </div>
        )}
        {!config.accounts.length && (
          <p className="text-sm text-muted-foreground">
            No profiles connected.
          </p>
        )}
        {config.accounts.map((account) => (
          <div key={account.id} className="space-y-3 rounded-lg border p-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <p className="text-sm font-medium">{account.name}</p>
              <div className="flex flex-wrap gap-2">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={discover.isPending}
                  onClick={() => discover.mutate(account.id)}
                >
                  Discover servers
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={locked("plex.accounts") || save.isPending}
                  onClick={() => setRemove({ kind: "account", id: account.id })}
                >
                  Disconnect
                </Button>
              </div>
            </div>
            <HomeProfiles account={account} locked={locked("plex.accounts")} />
          </div>
        ))}
      </SettingsPanel>
      {discovered && (
        <SettingsPanel title="Available Plex servers">
          {discovered.servers.map((server) => (
            <div
              key={server.handle}
              className="space-y-2 rounded-lg border p-3"
            >
              <p className="text-sm font-medium">
                {server.name} · {server.owned ? "Owned" : "Shared"}
              </p>
              {server.connections.map((connection) => (
                <div
                  key={connection.uri}
                  className="flex flex-wrap items-center justify-between gap-2"
                >
                  <span className="min-w-0 break-all text-xs text-muted-foreground">
                    {connection.uri} ·{" "}
                    {connection.local
                      ? "Local"
                      : connection.relay
                        ? "Relay"
                        : "Remote"}
                  </span>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={locked("plex.servers") || select.isPending}
                    onClick={() =>
                      select.mutate({
                        handle: server.handle,
                        url: connection.uri,
                      })
                    }
                  >
                    Connect server
                  </Button>
                </div>
              ))}
              {!server.connections.length && (
                <p className="text-sm text-muted-foreground">
                  No supported endpoint was advertised.
                </p>
              )}
            </div>
          ))}
        </SettingsPanel>
      )}
      <SettingsPanel title="Plex servers">
        {locked("plex.servers") && (
          <p className="text-sm text-muted-foreground">
            Servers are managed by the environment.
          </p>
        )}
        {!config.servers.length && (
          <p className="text-sm text-muted-foreground">
            Discover a server from a connected account.
          </p>
        )}
        {config.servers.map((server) => (
          <ServerSettings
            key={server.id}
            server={server}
            locked={locked("plex.servers")}
            update={update}
            onRemove={() => setRemove({ kind: "server", id: server.id })}
          />
        ))}
      </SettingsPanel>
    </div>
  );
}
function HomeProfiles({
  account,
  locked,
}: {
  account: Account;
  locked: boolean;
}) {
  const client = useQueryClient();
  const [selected, setSelected] = useState<string | null>(null);
  const [pin, setPin] = useState("");
  const users = useMutation({
    mutationFn: () =>
      body(
        api.GET("/api/v1/plex/accounts/{id}/home", {
          params: { path: { id: account.id } },
        }),
      ),
  });
  const connect = useMutation({
    mutationFn: ({ user, pin }: { user: string; pin: string | null }) =>
      body(
        api.POST("/api/v1/plex/accounts/{id}/home", {
          params: { path: { id: account.id } },
          body: { user_id: user, pin },
        }),
      ),
    onSuccess: () => {
      setSelected(null);
      setPin("");
      void client.invalidateQueries({ queryKey: ["settings"] });
    },
  });
  return (
    <div className="space-y-2">
      <Button
        type="button"
        variant="ghost"
        size="sm"
        disabled={users.isPending}
        onClick={() => users.mutate()}
      >
        Home profiles
      </Button>
      {users.error && <Failure error={users.error} />}{" "}
      {connect.error && <Failure error={connect.error} />}
      {users.data?.map((user) => (
        <div key={user.id} className="space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm">
              {user.name}
              {user.protected ? " · PIN protected" : ""}
            </span>
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={locked || connect.isPending}
              onClick={() => {
                setPin("");
                if (user.protected) setSelected(user.id);
                else connect.mutate({ user: user.id, pin: null });
              }}
            >
              Connect profile
            </Button>
          </div>
          {selected === user.id && (
            <div className="flex flex-wrap items-end gap-2">
              <Field id={`plex-home-${account.id}-${user.id}`} label="Home PIN">
                <Input
                  id={`plex-home-${account.id}-${user.id}`}
                  type="password"
                  inputMode="numeric"
                  maxLength={4}
                  value={pin}
                  {...notAPassword}
                  onChange={(event) =>
                    setPin(event.target.value.replace(/\D/g, ""))
                  }
                />
              </Field>
              <Button
                type="button"
                disabled={locked || pin.length !== 4 || connect.isPending}
                onClick={() => connect.mutate({ user: user.id, pin })}
              >
                Confirm profile
              </Button>
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
function ServerSettings({
  server,
  locked,
  update,
  onRemove,
}: {
  server: Server;
  locked: boolean;
  update: Update;
  onRemove: () => void;
}) {
  const [draft, setDraft] = useState(() => serverDefaults(server));
  const [base, setBase] = useState(() => serverDefaults(server));
  const [editing, setEditing] = useState(false);
  const save = useMutation({
    mutationFn: () =>
      update((current) => ({
        ...current,
        servers: current.servers.map((value) =>
          value.id === server.id
            ? {
                ...value,
                ...(draft.name !== base.name ? { name: draft.name } : {}),
                ...(draft.url !== base.url ? { url: draft.url } : {}),
                ...(draft.enabled !== base.enabled
                  ? { enabled: draft.enabled }
                  : {}),
                ...(JSON.stringify(draft.path_mappings) !==
                JSON.stringify(base.path_mappings)
                  ? { path_mappings: draft.path_mappings }
                  : {}),
              }
            : value,
        ),
      })),
    onSuccess: () => setEditing(false),
  });
  const libraries = useMutation({
    mutationFn: () =>
      body(
        api.GET("/api/v1/plex/servers/{id}/libraries", {
          params: { path: { id: server.id } },
        }),
      ),
  });
  const mapping = (
    index: number,
    change: Partial<Schemas["PlexPathMapping"]>,
  ) =>
    setDraft({
      ...draft,
      path_mappings: draft.path_mappings.map((value, i) =>
        i === index ? { ...value, ...change } : value,
      ),
    });
  return (
    <div className="space-y-3 rounded-lg border p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="text-sm font-medium">{server.name || server.id}</p>
          <p className="break-all text-xs text-muted-foreground">
            {server.url} · {(server.enabled ?? true) ? "Enabled" : "Disabled"}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={libraries.isPending}
            onClick={() => libraries.mutate()}
          >
            Test connection
          </Button>
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={locked || save.isPending}
            onClick={() => {
              setBase(serverDefaults(server));
              setDraft(serverDefaults(server));
              setEditing(!editing);
            }}
          >
            Edit
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={locked || save.isPending}
            onClick={onRemove}
          >
            Disconnect
          </Button>
        </div>
      </div>
      {libraries.error && <Failure error={libraries.error} />}{" "}
      {libraries.data && (
        <p className="text-sm text-muted-foreground">
          Connection test passed · {libraries.data.length} supported libraries
        </p>
      )}
      {editing && (
        <div className="space-y-3">
          <Field id={`plex-${server.id}-name`} label="Server name">
            <Input
              id={`plex-${server.id}-name`}
              value={draft.name}
              disabled={locked || save.isPending}
              onChange={(event) =>
                setDraft({ ...draft, name: event.target.value })
              }
            />
          </Field>
          <Field id={`plex-${server.id}-url`} label="Server URL">
            <Input
              id={`plex-${server.id}-url`}
              value={draft.url}
              disabled={locked || save.isPending}
              onChange={(event) =>
                setDraft({ ...draft, url: event.target.value })
              }
            />
          </Field>
          <Toggle
            id={`plex-${server.id}-enabled`}
            label="Enabled"
            checked={draft.enabled}
            disabled={locked || save.isPending}
            onChange={(enabled) => setDraft({ ...draft, enabled })}
          />
          <p className="text-sm text-muted-foreground">
            Map exact Plex folders to this installation's DAV paths or local
            library folders.
          </p>
          {draft.path_mappings.map((value, index) => (
            <div key={index} className="space-y-2 rounded-lg border p-3">
              <Field
                id={`plex-${server.id}-${index}-source`}
                label="Plex folder"
              >
                <Input
                  id={`plex-${server.id}-${index}-source`}
                  value={value.plex_path}
                  disabled={locked || save.isPending}
                  onChange={(event) =>
                    mapping(index, { plex_path: event.target.value })
                  }
                />
              </Field>
              <Field id={`plex-${server.id}-${index}-kind`} label="Target type">
                <Select
                  value={value.local_path != null ? "local" : "dav"}
                  disabled={locked || save.isPending}
                  onValueChange={(kind) =>
                    mapping(
                      index,
                      kind === "local"
                        ? {
                            local_path:
                              value.dav_path || value.local_path || "",
                            dav_path: null,
                          }
                        : {
                            dav_path: value.local_path || value.dav_path || "",
                            local_path: null,
                          },
                    )
                  }
                >
                  <SelectTrigger id={`plex-${server.id}-${index}-kind`}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="dav">DAV path</SelectItem>
                    <SelectItem value="local">Local library path</SelectItem>
                  </SelectContent>
                </Select>
              </Field>
              <Field
                id={`plex-${server.id}-${index}-target`}
                label="Target path"
              >
                <Input
                  id={`plex-${server.id}-${index}-target`}
                  value={value.dav_path || value.local_path || ""}
                  disabled={locked || save.isPending}
                  onChange={(event) =>
                    mapping(
                      index,
                      value.local_path != null
                        ? { local_path: event.target.value }
                        : { dav_path: event.target.value },
                    )
                  }
                />
              </Field>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={locked || save.isPending}
                onClick={() =>
                  setDraft({
                    ...draft,
                    path_mappings: draft.path_mappings.filter(
                      (_, i) => i !== index,
                    ),
                  })
                }
              >
                Remove mapping
              </Button>
            </div>
          ))}
          <Button
            type="button"
            variant="outline"
            disabled={locked || draft.path_mappings.length >= 64}
            onClick={() =>
              setDraft({
                ...draft,
                path_mappings: [
                  ...draft.path_mappings,
                  { plex_path: "", dav_path: "", local_path: null },
                ],
              })
            }
          >
            Add path mapping
          </Button>
          {save.error && <Failure error={save.error} />}
          <div className="flex gap-2">
            <Button
              type="button"
              disabled={locked || save.isPending || !draft.url}
              onClick={() => save.mutate()}
            >
              Save server
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={save.isPending}
              onClick={() => setEditing(false)}
            >
              Cancel edit
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
