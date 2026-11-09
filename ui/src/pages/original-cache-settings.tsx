import type { Schemas } from "../api/client";
import { Field, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
  const disabled = locked("native_cache.legacy");
  const patch = (change: Partial<Settings>) => {
    if (value) onChange({ ...value, ...change });
  };
  return (
    <SettingsPanel title="Existing v1 cache storage">
      <p className="text-sm text-muted-foreground">
        Reuse existing folders and their catalogue in place. Nothing is copied
        or converted. Storage changes require a restart and exclusive ownership
        of the cache.
      </p>
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
            enabled ? { metadata_path: "", folders: [], write: null } : null,
          )
        }
      />
      {value && (
        <>
          <details className="rounded-lg border p-4">
            <summary className="cursor-pointer text-sm font-medium">
              Metadata directory
            </summary>
            <div className="mt-3">
              <Field
                id="legacy-metadata"
                label="Original metadata directory"
                hint="Contains catalogue.db and repair-revisions.db."
              >
                <Input
                  id="legacy-metadata"
                  value={value.metadata_path}
                  disabled={disabled}
                  onChange={(e) => patch({ metadata_path: e.target.value })}
                />
              </Field>
            </div>
          </details>
          <Toggle
            id="legacy-compatible-write"
            label="Enable compatible writes"
            hint="Writes verified blocks in the existing v1 format. Select folders and review their quotas before saving. Existing blocks are retained."
            checked={!!value.write}
            disabled={disabled}
            onChange={(enabled) =>
              patch({ write: enabled ? { chunk_mb: 0, folders: [] } : null })
            }
          />
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
          {value.folders.map((folder, index) => {
            const write = value.write?.folders.find((f) => f.id === folder.id);
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
            return (
              <details key={index} className="rounded-lg border bg-card">
                <summary className="cursor-pointer p-4 text-sm font-medium">
                  {folder.id || `Original folder ${index + 1}`}
                  <span className="ml-2 text-xs font-normal text-muted-foreground">
                    {write
                      ? `${(write.quota_bytes / 1000000000).toLocaleString()} GB quota · priority ${write.priority}`
                      : "Read only"}
                  </span>
                  <span className="mt-1 block break-all font-mono text-xs font-normal text-muted-foreground">
                    {folder.path || "Existing path not set"}
                  </span>
                </summary>
                <div className="space-y-3 border-t p-4">
                  <Field
                    label="Registered folder ID"
                    hint="Use the existing catalogue identity, not a new name."
                  >
                    <Input
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
                      aria-label={`Original folder ${index + 1} path`}
                      disabled={disabled}
                      value={folder.path}
                      onChange={(e) =>
                        patch({
                          folders: value.folders.map((f, i) =>
                            i === index ? { ...f, path: e.target.value } : f,
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
                                    owner_lock_path: e.target.value || null,
                                  }
                                : f,
                            ),
                          })
                        }
                      />
                    </Field>
                  )}
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
                      <div className="grid gap-3 sm:grid-cols-2">
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
                                min_free_bytes: Number(e.target.value) * 1e9,
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
                                max={key === "max_age_days" ? undefined : 100}
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
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={disabled}
                    onClick={() =>
                      patch({
                        folders: value.folders.filter((_, i) => i !== index),
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
            );
          })}
          <Button
            type="button"
            variant="outline"
            disabled={disabled}
            onClick={() =>
              patch({ folders: [...value.folders, { id: "", path: "" }] })
            }
          >
            Add existing folder
          </Button>
        </>
      )}
    </SettingsPanel>
  );
}
