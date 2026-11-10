import type { Schemas } from "../api/client";
import { Failure, Field, Locked, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Link } from "react-router";
import { Badge } from "@/components/ui/badge";
import { HardDrive, ChevronRight } from "lucide-react";
import { bytes } from "../format";
import { CacheMeter } from "./cache-range-map";
import { useOriginalCacheOverview } from "./original-cache-overview";
type Settings = Schemas["LegacyCacheSettings"];
export function OriginalCacheSettings({
  value,
  locked,
  onChange,
}: {
  value: Settings | null | undefined;
  locked: (path: string) => boolean;
  onChange: (v: Settings | null) => void;
}) {
  const inventory = useOriginalCacheOverview(!!value);
  const liveFolders =
    value?.folders.map((folder) =>
      (!inventory.error ? inventory.data : undefined)?.folders.find(
        (f) => f.id === folder.id && f.path === folder.path,
      ),
    ) ?? [];
  const known =
    !inventory.error && !!inventory.data && liveFolders.every(Boolean);
  const allocated = known
    ? liveFolders.reduce((n, f) => n + (f?.allocated_bytes ?? 0), 0)
    : null;
  const held = known
    ? liveFolders.reduce((n, f) => n + (f?.held_files ?? 0), 0)
    : null;
  const quota =
    value?.folders.reduce(
      (n, f) =>
        n + (value.write?.folders.find((w) => w.id === f.id)?.quota_bytes ?? 0),
      0,
    ) ?? 0;
  const disabled = locked("native_cache.legacy");
  const patch = (change: Partial<Settings>) => {
    if (value) onChange({ ...value, ...change });
  };
  return (
    <section aria-label="Existing cache settings" className="space-y-5">
      <div className="overflow-hidden rounded-lg border bg-card">
        <div className="flex flex-wrap items-start justify-between gap-3 p-4">
          <div className="flex min-w-0 gap-3">
            <HardDrive
              aria-hidden="true"
              className="mt-0.5 size-5 shrink-0 text-muted-foreground"
            />
            <div className="space-y-1">
              <h3 className="text-sm font-medium">Existing cache storage</h3>
              <p className="max-w-xl text-xs text-muted-foreground">
                Use the same folders and catalogue as v1. Existing media stays
                in place.
              </p>
            </div>
          </div>
          {value && (
            <Badge variant="secondary">
              {inventory.error
                ? "Storage unavailable"
                : inventory.data
                  ? inventory.data.writable
                    ? "Running: compatible writes"
                    : "Running: read only"
                  : "Checking running storage"}
            </Badge>
          )}
        </div>
        <div className="space-y-4 border-t p-4">
          <Toggle
            id="legacy-cache-configured"
            label={
              <>
                Use existing v1 cache <Locked locked={disabled} />
              </>
            }
            checked={!!value}
            disabled={disabled}
            onChange={(enabled) =>
              onChange(
                enabled
                  ? { metadata_path: "", folders: [], write: null }
                  : null,
              )
            }
          />
          {value && (
            <>
              <Toggle
                id="legacy-compatible-write"
                label="Enable compatible writes"
                hint="Writes verified blocks in the existing v1 format. Select folders and review their quotas before saving. Existing blocks are retained."
                checked={!!value.write}
                disabled={disabled}
                onChange={(enabled) =>
                  patch({
                    write: enabled ? { chunk_mb: 0, folders: [] } : null,
                  })
                }
              />
            </>
          )}
        </div>
      </div>
      {inventory.error && value && <Failure error={inventory.error} />}
      {value && (
        <>
          <div
            className="grid grid-cols-2 gap-3 xl:grid-cols-4"
            aria-label="Cache storage summary"
          >
            {[
              [
                "Allocated storage",
                allocated === null ? "—" : bytes(allocated),
                "Running catalogue",
              ],
              [
                "Configured quota",
                value.write ? bytes(quota) : "Read only",
                "Selected write folders",
              ],
              [
                "Cache folders",
                String(value.folders.length),
                "Existing storage locations",
              ],
              [
                "Protected files",
                held === null ? "—" : held.toLocaleString(),
                "Pinned, active or held",
              ],
            ].map(([label, amount, hint]) => (
              <div
                key={label}
                className="min-w-0 rounded-lg border bg-card p-4"
              >
                <p className="text-xs text-muted-foreground">{label}</p>
                <p className="mt-2 break-words font-mono text-lg font-medium tabular-nums">
                  {amount}
                </p>
                <p className="mt-1 text-xs text-muted-foreground">{hint}</p>
              </div>
            ))}
          </div>
          <section aria-labelledby="cache-folders-title" className="space-y-3">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <h3 id="cache-folders-title" className="text-sm font-medium">
                  Cache folders
                </h3>
                <p className="mt-1 text-xs text-muted-foreground">
                  Running usage with configured capacity, placement and
                  retention.
                </p>
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={disabled}
                onClick={() =>
                  patch({ folders: [...value.folders, { id: "", path: "" }] })
                }
              >
                Add existing folder
              </Button>
            </div>
            {value.folders.length === 0 && (
              <p className="rounded-lg border border-dashed p-5 text-sm text-muted-foreground">
                Add an existing folder using its registered ID and payload path.
              </p>
            )}
            {value.folders.map((folder, index) => {
              const write = value.write?.folders.find(
                (f) => f.id === folder.id,
              );
              const update = (
                change: Partial<Schemas["LegacyCacheWriteFolder"]>,
              ) => {
                if (value.write && write)
                  patch({
                    write: {
                      ...value.write,
                      folders: value.write.folders.map((f) =>
                        f.id === folder.id ? { ...f, ...change } : f,
                      ),
                    },
                  });
              };
              const live = liveFolders[index];
              const limit = write?.quota_bytes ?? live?.quota_bytes;
              return (
                <article
                  key={index}
                  className="overflow-hidden rounded-lg border bg-card"
                >
                  <div className="flex flex-wrap items-start justify-between gap-3 p-4">
                    <div className="min-w-0 space-y-1">
                      <h4 className="break-all text-sm font-medium">
                        {folder.id || `Original folder ${index + 1}`}
                      </h4>
                      <p className="break-all font-mono text-xs text-muted-foreground">
                        {folder.path || "Existing path not set"}
                      </p>
                    </div>
                    <div className="flex flex-wrap gap-2">
                      <Badge variant="secondary">
                        {live
                          ? live.writable
                            ? "Writes permitted"
                            : "Read only"
                          : inventory.isPending
                            ? "Checking storage"
                            : "Not active"}
                      </Badge>
                      <Badge variant="outline">
                        {write?.eviction
                          ? "Eviction selected"
                          : "Eviction disabled"}
                      </Badge>
                    </div>
                  </div>
                  <div className="space-y-2 px-4 pb-4">
                    <div className="flex flex-wrap items-baseline justify-between gap-2 text-xs tabular-nums">
                      <p>
                        <span className="font-medium">
                          {live
                            ? bytes(live.allocated_bytes)
                            : "Usage unavailable"}
                        </span>
                        {limit != null && <> / {bytes(limit)} quota</>}
                      </p>
                      <span className="text-muted-foreground">
                        {live
                          ? `${live.files.toLocaleString()} files · ${live.held_files.toLocaleString()} protected`
                          : "Save and restart to apply storage changes"}
                      </span>
                    </div>
                    {live && limit != null && limit > 0 && (
                      <CacheMeter
                        label={`${folder.id || `Original folder ${index + 1}`} cache capacity`}
                        percent={(live.allocated_bytes / limit) * 100}
                      />
                    )}
                    {write && (
                      <p className="text-xs text-muted-foreground">
                        Priority {write.priority} · keep{" "}
                        {bytes(write.min_free_bytes)} free
                      </p>
                    )}
                  </div>
                  <details className="group/folder border-t bg-muted/20">
                    <summary className="flex cursor-pointer list-none items-center gap-2 px-4 py-3 text-xs font-medium [&::-webkit-details-marker]:hidden focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
                      <ChevronRight
                        aria-hidden="true"
                        className="size-3.5 shrink-0 transition-transform group-open/folder:rotate-90"
                      />
                      Configure {folder.id || `original folder ${index + 1}`}
                    </summary>
                    <div className="space-y-3 border-t p-4">
                      {value.write && (
                        <Toggle
                          id={`legacy-write-${index}`}
                          label="Allow compatible writes to this folder"
                          disabled={disabled}
                          checked={!!write}
                          onChange={(enabled) =>
                            patch({
                              write: {
                                ...value.write!,
                                folders: enabled
                                  ? [
                                      ...value.write!.folders,
                                      {
                                        id: folder.id,
                                        priority: 0,
                                        quota_bytes: 100e9,
                                        min_free_bytes: 1e9,
                                      },
                                    ]
                                  : value.write!.folders.filter(
                                      (f) => f.id !== folder.id,
                                    ),
                              },
                            })
                          }
                        />
                      )}
                      {write && (
                        <>
                          <div className="grid gap-4 sm:grid-cols-3">
                            <Field label="Quota (GB)">
                              <Input
                                aria-label={`Original folder ${index + 1} quota`}
                                type="number"
                                step="any"
                                min={1}
                                disabled={disabled}
                                value={write.quota_bytes / 1e9}
                                onChange={(e) =>
                                  update({
                                    quota_bytes: Number(e.target.value) * 1e9,
                                  })
                                }
                              />
                            </Field>
                            <Field label="Keep free (GB)">
                              <Input
                                aria-label={`Original folder ${index + 1} reserve`}
                                type="number"
                                step="any"
                                min={0}
                                disabled={disabled}
                                value={write.min_free_bytes / 1e9}
                                onChange={(e) =>
                                  update({
                                    min_free_bytes:
                                      Number(e.target.value) * 1e9,
                                  })
                                }
                              />
                            </Field>
                            <Field label="Placement priority">
                              <Input
                                aria-label={`Original folder ${index + 1} priority`}
                                type="number"
                                disabled={disabled}
                                value={write.priority}
                                onChange={(e) =>
                                  update({ priority: Number(e.target.value) })
                                }
                              />
                            </Field>
                          </div>
                          <Toggle
                            id={`legacy-eviction-${index}`}
                            label="Allow eviction"
                            hint="Permits policy and confirmed manual removal of unpinned, inactive cached copies."
                            checked={!!write.eviction}
                            disabled={disabled}
                            onChange={(enabled) =>
                              update({
                                eviction: enabled
                                  ? {
                                      max_age_days: 0,
                                      high_percent: 90,
                                      low_percent: 80,
                                    }
                                  : null,
                              })
                            }
                          />
                          {write.eviction && (
                            <div className="grid gap-3 sm:grid-cols-3">
                              {(
                                [
                                  ["max_age_days", "Maximum age (days)"],
                                  ["high_percent", "High watermark (%)"],
                                  ["low_percent", "Low watermark (%)"],
                                ] as const
                              ).map(([key, label]) => (
                                <Field key={key} label={label}>
                                  <Input
                                    aria-label={`Original folder ${index + 1} ${label}`}
                                    type="number"
                                    min={key === "max_age_days" ? 0 : 1}
                                    max={
                                      key === "max_age_days" ? undefined : 100
                                    }
                                    disabled={disabled}
                                    value={write.eviction![key]}
                                    onChange={(e) =>
                                      update({
                                        eviction: {
                                          ...write.eviction!,
                                          [key]: Number(e.target.value),
                                        },
                                      })
                                    }
                                  />
                                </Field>
                              ))}
                            </div>
                          )}
                        </>
                      )}
                      <details className="border-t pt-3">
                        <summary className="cursor-pointer text-xs font-medium">
                          Folder identity and paths
                        </summary>
                        <div className="mt-4 space-y-4">
                          <Field
                            label="Registered folder ID"
                            hint="Use the existing catalogue identity, not a new name."
                          >
                            <Input
                              className="font-mono"
                              aria-label={`Original folder ${index + 1} ID`}
                              disabled={disabled}
                              value={folder.id}
                              onChange={(e) => {
                                const id = e.target.value;
                                patch({
                                  folders: value.folders.map((f, i) =>
                                    i === index ? { ...f, id } : f,
                                  ),
                                  write: value.write
                                    ? {
                                        ...value.write,
                                        folders: value.write.folders.map((f) =>
                                          f.id === folder.id ? { ...f, id } : f,
                                        ),
                                      }
                                    : null,
                                });
                              }}
                            />
                          </Field>
                          <Field label="Existing payload path">
                            <Input
                              className="font-mono"
                              aria-label={`Original folder ${index + 1} path`}
                              disabled={disabled}
                              value={folder.path}
                              onChange={(e) =>
                                patch({
                                  folders: value.folders.map((f, i) =>
                                    i === index
                                      ? { ...f, path: e.target.value }
                                      : f,
                                  ),
                                })
                              }
                            />
                          </Field>
                          {folder.owner_lock_path != null && (
                            <Field
                              label="Owner lock path"
                              hint="An optional writable bind of the same owner inode for read-only NFS mounts."
                            >
                              <Input
                                aria-label={`Original folder ${index + 1} owner lock`}
                                disabled={disabled}
                                value={folder.owner_lock_path}
                                onChange={(e) =>
                                  patch({
                                    folders: value.folders.map((f, i) =>
                                      i === index
                                        ? {
                                            ...f,
                                            owner_lock_path:
                                              e.target.value || null,
                                          }
                                        : f,
                                    ),
                                  })
                                }
                              />
                            </Field>
                          )}
                        </div>
                      </details>
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        disabled={disabled}
                        onClick={() =>
                          patch({
                            folders: value.folders.filter(
                              (_, i) => i !== index,
                            ),
                            write: value.write
                              ? {
                                  ...value.write,
                                  folders: value.write.folders.filter(
                                    (f) => f.id !== folder.id,
                                  ),
                                }
                              : null,
                          })
                        }
                      >
                        Remove folder from configuration
                      </Button>
                    </div>
                  </details>
                </article>
              );
            })}
          </section>
          <details className="rounded-lg border bg-card">
            <summary className="cursor-pointer p-4 text-sm font-medium">
              Storage and write settings
              <span className="mt-1 block text-xs font-normal text-muted-foreground">
                Catalogue location and layout for new cache entries.
              </span>
            </summary>
            <div className="space-y-4 border-t p-4">
              <div>
                <div className="space-y-3">
                  <Field
                    id="legacy-metadata"
                    label="Original metadata directory"
                    hint="Contains catalogue.db and repair-revisions.db."
                  >
                    <Input
                      className="font-mono"
                      id="legacy-metadata"
                      value={value.metadata_path}
                      disabled={disabled}
                      onChange={(e) => patch({ metadata_path: e.target.value })}
                    />
                  </Field>
                </div>
              </div>
              {value.write && (
                <Field
                  label="Chunk size (MB)"
                  hint="Zero keeps sparse files. Existing entries retain their layout."
                >
                  <Input
                    aria-label="Original cache chunk size"
                    type="number"
                    min={0}
                    max={256}
                    value={value.write.chunk_mb}
                    disabled={disabled}
                    onChange={(e) =>
                      patch({
                        write: {
                          ...value.write!,
                          chunk_mb: Number(e.target.value),
                        },
                      })
                    }
                  />
                </Field>
              )}
            </div>
          </details>
          <div className="flex flex-wrap gap-3 text-xs">
            <Link
              className="text-primary underline underline-offset-4"
              to="/native-cache?tab=files"
            >
              View cached files
            </Link>
            <Link
              className="text-primary underline underline-offset-4"
              to="/native-cache?tab=diagnostics"
            >
              Storage diagnostics
            </Link>
          </div>
        </>
      )}
    </section>
  );
}
