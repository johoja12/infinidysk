import { createContext, lazy, Suspense, useContext, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router";

const FileDetailsModal = lazy(() => import("./pages/file-details-modal"));

const OpenFile = createContext<((item: string, trigger: HTMLAnchorElement | null) => void) | null>(null);

/** Selection outlives the activity row when a read closes or warming completes. */
export function FileDetailsProvider({ children }: { children: ReactNode }) {
  const [selection, setSelection] = useState<{ item: string; trigger: HTMLAnchorElement | null }>();
  return (
    <OpenFile.Provider value={(item, trigger) => setSelection({ item, trigger })}>
      {children}
      {selection && <Suspense fallback={null}>
        <FileDetailsModal key={selection.item} id={selection.item} onClose={() => setSelection(undefined)} onRestoreFocus={() => selection.trigger?.focus()} />
      </Suspense>}
    </OpenFile.Provider>
  );
}

/** Keep a real destination for modified clicks and keyboard navigation. */
export function FileLink({ item, children, className }: {
  item?: string | null;
  children: ReactNode;
  className?: string;
}) {
  const openFile = useContext(OpenFile);
  const trigger = useRef<HTMLAnchorElement>(null);
  const [open, setOpen] = useState(false);
  if (!item) return <span className={className}>{children}</span>;
  return (
    <>
      <Link
        ref={trigger}
        to={`/explorer?item=${encodeURIComponent(item)}`}
        className={className}
        onClick={(event) => {
          if (event.button || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
          event.preventDefault();
          event.stopPropagation();
          if (openFile) openFile(item, trigger.current);
          else setOpen(true);
        }}
      >
        {children}
      </Link>
      {open && <Suspense fallback={null}>
        <FileDetailsModal id={item} onClose={() => setOpen(false)} onRestoreFocus={() => trigger.current?.focus()} />
      </Suspense>}
    </>
  );
}
