import { SettingsIntro } from "~/components/ui";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import {
  canGenerateCanaryPlan,
  canReconcileNzbDav,
  isRunConfirmationExact,
  useNzbDavMigration,
  type NzbDavCategory,
  type NzbDavConnectForm,
  type NzbDavConnection,
  type NzbDavCorrelation,
  type NzbDavFullStatus,
} from "./use-nzbdav-migration";

export type NzbDavMigrationViewProps = {
  form: NzbDavConnectForm;
  onFormChange: (form: NzbDavConnectForm) => void;
  connection: NzbDavConnection | null;
  sessionStatus: string | undefined;
  categories: NzbDavCategory[];
  onCategoryChange: (source: string, change: Partial<NzbDavCategory>) => void;
  correlation: NzbDavCorrelation | null;
  fullStatus: NzbDavFullStatus | null;
  digestConfirmation: string;
  countConfirmation: string;
  onDigestConfirmationChange: (value: string) => void;
  onCountConfirmationChange: (value: string) => void;
  planReady: boolean;
  busy: string | null;
  error: string | null;
  onConnect: () => void;
  onSaveCategories: () => void;
  onScan: () => void;
  onRun: () => void;
  onLoadCorrelation: () => void;
  onReconcile: () => void;
  onGeneratePlan: () => void;
};

const STEPS = ["Package", "Review", "Import", "Correlate", "Canary plan"];

export function NzbDavMigrationView(props: NzbDavMigrationViewProps) {
  const categoriesReady =
    props.categories.length > 0 &&
    props.categories.every(
      (category) => category.action === "exclude" || category.target.trim() !== "",
    );
  const runConfirmed = isRunConfirmationExact(
    props.connection,
    props.digestConfirmation,
    props.countConfirmation,
  );
  const planAllowed = canGenerateCanaryPlan(props.sessionStatus, props.correlation);
  const reconcileAllowed = canReconcileNzbDav(props.sessionStatus, props.correlation);

  return (
    <div className="flex w-full flex-col gap-6">
      <SettingsIntro>
        Import a reviewed legacy NzbDav export without reading its PostgreSQL database from
        InfiniDysk. The package is verified first, then submitted through the ordinary queue and
        correlated by article identity.
      </SettingsIntro>

      <ul className="steps w-full text-xs">
        {STEPS.map((step) => (
          <li key={step} className="step step-primary">
            {step}
          </li>
        ))}
      </ul>

      <Alert className="alert-soft text-sm" variant="warning">
        <Icon name="warning" className="!text-[18px]" />
        <span>
          <code>/mnt/plex2</code> must remain outside Plex and Arr scanning. Download the immutable
          plan here, then apply it on nuc-1 with the host CLI. InfiniDysk never writes those links.
        </span>
      </Alert>

      {props.error && <div className="alert alert-error text-sm">{props.error}</div>}

      {props.fullStatus && (
        <section className="rounded-box border border-base-300 p-4 space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="font-semibold">Full-library recovery progress</h3>
            <span
              className={`badge ${props.fullStatus.coverage >= 0.9 ? "badge-success" : "badge-error"}`}
            >
              {(props.fullStatus.coverage * 100).toFixed(1)}% recoverable
            </span>
          </div>
          <div className="flex flex-wrap gap-2 text-xs">
            <span className="badge">{props.fullStatus.sourceLinkCount} source links</span>
            <span className="badge badge-success">{props.correlation?.exactCount ?? 0} exact</span>
            <span className="badge">{props.fullStatus.appliedCount} applied</span>
            <span className="badge">{props.fullStatus.validatedCount} validated</span>
          </div>
          <div className="overflow-x-auto">
            <table className="table table-xs">
              <thead>
                <tr>
                  <th>Batch</th>
                  <th>Status</th>
                  <th>Selected</th>
                  <th>Applied</th>
                  <th>Validated</th>
                </tr>
              </thead>
              <tbody>
                {props.fullStatus.batches.map((batch) => (
                  <tr key={batch.batchIndex}>
                    <td>{batch.batchIndex + 1}</td>
                    <td>{batch.status}</td>
                    <td>{batch.selectionCount}</td>
                    <td>{batch.appliedCount}</td>
                    <td>{batch.validatedCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      <section className="rounded-box border border-base-300 p-4 space-y-3">
        <h3 className="font-semibold">1. Validate package</h3>
        <label className="form-control">
          <span className="label-text text-xs">Package path inside the container</span>
          <input
            aria-label="Package path"
            className="input input-bordered"
            value={props.form.packagePath}
            onChange={(event) =>
              props.onFormChange({ ...props.form, packagePath: event.target.value })
            }
          />
        </label>
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="form-control">
            <span className="label-text text-xs">Queue depth</span>
            <input
              aria-label="Queue depth"
              className="input input-bordered"
              type="number"
              value={props.form.maxQueueDepth}
              onChange={(event) =>
                props.onFormChange({ ...props.form, maxQueueDepth: Number(event.target.value) })
              }
            />
          </label>
          <label className="form-control">
            <span className="label-text text-xs">Submit workers</span>
            <input
              aria-label="Submit workers"
              className="input input-bordered"
              type="number"
              value={props.form.submitWorkers}
              onChange={(event) =>
                props.onFormChange({ ...props.form, submitWorkers: Number(event.target.value) })
              }
            />
          </label>
        </div>
        <button
          className="btn btn-primary"
          disabled={props.busy !== null}
          onClick={props.onConnect}
        >
          Validate immutable package
        </button>
        {props.connection && (
          <div className="rounded-box bg-base-200 p-3 text-sm space-y-1">
            <div className="flex gap-2">
              <span className="badge badge-success">
                {props.connection.selectionCount} selected
              </span>
              <span className="badge badge-warning">
                {props.connection.exclusionCount} excluded
              </span>
              <span className="badge">{props.connection.releaseCount} releases</span>
            </div>
            <p className="break-all font-mono text-xs">{props.connection.packageDigest}</p>
          </div>
        )}
      </section>

      <section className="rounded-box border border-base-300 p-4 space-y-3">
        <h3 className="font-semibold">2. Review category isolation</h3>
        <p className="text-sm text-warning">
          Map every imported source category to dedicated empty InfiniDysk categories. Do not reuse
          production Arr categories during the canary.
        </p>
        {props.categories.map((category) => (
          <div key={category.source} className="grid gap-2 sm:grid-cols-[1fr_1fr_auto]">
            <code className="self-center text-xs">{category.source}</code>
            <input
              aria-label={`Target category for ${category.source}`}
              className="input input-bordered input-sm"
              placeholder="nzbdav-canary-tv"
              value={category.target}
              disabled={category.action === "exclude"}
              onChange={(event) =>
                props.onCategoryChange(category.source, { target: event.target.value })
              }
            />
            <select
              aria-label={`Action for ${category.source}`}
              className="select select-bordered select-sm"
              value={category.action}
              onChange={(event) =>
                props.onCategoryChange(category.source, {
                  action: event.target.value as NzbDavCategory["action"],
                })
              }
            >
              <option value="migrate">Migrate</option>
              <option value="exclude">Exclude</option>
            </select>
          </div>
        ))}
        <div className="flex gap-2">
          <button
            className="btn"
            disabled={!categoriesReady || props.busy !== null}
            onClick={props.onSaveCategories}
          >
            Save isolated mappings
          </button>
          <button
            className="btn"
            disabled={!categoriesReady || props.busy !== null}
            onClick={props.onScan}
          >
            Scan package
          </button>
        </div>
      </section>

      <section className="rounded-box border border-base-300 p-4 space-y-3">
        <h3 className="font-semibold">3. Confirm and import</h3>
        <p className="text-xs text-base-content/60">
          Type the complete immutable digest and exact selected count. There is no override for a
          mismatch.
        </p>
        <input
          aria-label="Confirm package digest"
          className="input input-bordered w-full font-mono text-xs"
          value={props.digestConfirmation}
          onChange={(event) => props.onDigestConfirmationChange(event.target.value)}
        />
        <input
          aria-label="Confirm selection count"
          className="input input-bordered w-full"
          value={props.countConfirmation}
          onChange={(event) => props.onCountConfirmationChange(event.target.value)}
        />
        <button
          className="btn btn-primary"
          disabled={!runConfirmed || props.busy !== null}
          onClick={props.onRun}
        >
          Run NzbDav import
        </button>
      </section>

      <section className="rounded-box border border-base-300 p-4 space-y-3">
        <div className="flex items-center justify-between gap-2">
          <h3 className="font-semibold">4. Correlate every selected leaf</h3>
          <button
            className="btn btn-sm"
            disabled={props.busy !== null}
            onClick={props.onLoadCorrelation}
          >
            Load correlation report
          </button>
          <button
            className="btn btn-sm"
            disabled={!reconcileAllowed || props.busy !== null}
            onClick={props.onReconcile}
          >
            Reconcile completed import
          </button>
        </div>
        {props.correlation && (
          <>
            <div className="flex flex-wrap gap-2 text-xs">
              <span className="badge">{props.correlation.selectedCount} selected</span>
              <span className="badge badge-success">{props.correlation.exactCount} exact</span>
              <span className="badge badge-warning">
                {props.correlation.exclusionCount} excluded
              </span>
              <span className="badge badge-error">
                {props.correlation.ambiguityCount} ambiguous
              </span>
            </div>
            <div className="overflow-x-auto">
              <table className="table table-xs">
                <thead>
                  <tr>
                    <th>File</th>
                    <th>Extraction</th>
                    <th>Correlation</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {props.correlation.rows.map((row) => (
                    <tr key={`${row.libraryRelativePath}:${row.legacyDavItemId}`}>
                      <td>{row.libraryRelativePath}</td>
                      <td>{row.extractionStatus}</td>
                      <td>{row.correlationStatus}</td>
                      <td>{row.exclusionReason ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </section>

      <section className="rounded-box border border-base-300 p-4 space-y-3">
        <h3 className="font-semibold">5. Canary plan</h3>
        {props.correlation && props.correlation.ambiguityCount > 0 && (
          <p className="text-sm text-error">
            Plan generation is locked until every ambiguous or duplicate correlation is resolved.
          </p>
        )}
        <button
          className="btn btn-primary"
          disabled={!planAllowed || props.busy !== null}
          onClick={props.onGeneratePlan}
        >
          Generate canary plan
        </button>
        {props.planReady && (
          <a className="btn btn-outline ml-2" href="/api/migration/nzbdav/canary-plan" download>
            Download canary plan
          </a>
        )}
      </section>
    </div>
  );
}

export function NzbDavMigration() {
  const migration = useNzbDavMigration();
  return (
    <NzbDavMigrationView
      form={migration.form}
      onFormChange={migration.setForm}
      connection={migration.connection}
      sessionStatus={migration.sessionStatus}
      categories={migration.categories}
      onCategoryChange={migration.onCategoryChange}
      correlation={migration.correlation}
      fullStatus={migration.fullStatus}
      digestConfirmation={migration.digestConfirmation}
      countConfirmation={migration.countConfirmation}
      onDigestConfirmationChange={migration.setDigestConfirmation}
      onCountConfirmationChange={migration.setCountConfirmation}
      planReady={migration.planReady}
      busy={migration.busy}
      error={migration.error}
      onConnect={() => void migration.connect()}
      onSaveCategories={() => void migration.saveCategories()}
      onScan={() => void migration.scan()}
      onRun={() => void migration.run()}
      onLoadCorrelation={() => void migration.loadCorrelation()}
      onReconcile={() => void migration.reconcile()}
      onGeneratePlan={() => void migration.generatePlan()}
    />
  );
}
