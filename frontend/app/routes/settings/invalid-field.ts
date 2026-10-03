/** Classes and attributes the settings forms use to mark a field that blocks saving. */
const INVALID_SELECTOR = ".input-error, .select-error, .textarea-error, [aria-invalid='true']";

export type InvalidField = { element: HTMLElement; label: string };

/**
 * The first field on the page marked invalid, with a human label: its `<label for>`, an
 * enclosing `<label>`, its aria-label, or its placeholder. Null when no invalid field is rendered,
 * for example when it sits on another tab.
 */
export function findFirstInvalidField(root: ParentNode = document): InvalidField | null {
  const element = root.querySelector<HTMLElement>(INVALID_SELECTOR);
  if (!element) return null;
  const owner = element.ownerDocument;
  const byFor = element.id
    ? owner.querySelector(`label[for="${CSS.escape(element.id)}"]`)?.textContent
    : null;
  const label =
    byFor?.trim() ||
    element.closest("label")?.textContent?.trim() ||
    element.getAttribute("aria-label")?.trim() ||
    element.getAttribute("placeholder")?.trim() ||
    "";
  return { element, label: label.replace(/\s+/g, " ") };
}

/** Brings the first invalid field into view and focuses it; false when none is rendered. */
export function revealFirstInvalidField(root: ParentNode = document): boolean {
  const field = findFirstInvalidField(root);
  if (!field) return false;
  field.element.scrollIntoView?.({ behavior: "smooth", block: "center" });
  field.element.focus({ preventScroll: true });
  return true;
}
