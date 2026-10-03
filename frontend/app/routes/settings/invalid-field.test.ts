// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { findFirstInvalidField, revealFirstInvalidField } from "./invalid-field";

afterEach(() => {
  document.body.innerHTML = "";
});

describe("findFirstInvalidField", () => {
  it("names the first invalid field by its label", () => {
    document.body.innerHTML = `
      <label for="ok">Streaming Priority</label><input id="ok" class="input" />
      <label for="buf">Article Buffer Size</label><input id="buf" class="input input-error" placeholder="40" />
      <label for="late">Read Timeout</label><input id="late" class="input input-error" />`;
    expect(findFirstInvalidField()?.label).toBe("Article Buffer Size");
  });

  it("falls back to an enclosing label, then aria-label, then placeholder", () => {
    document.body.innerHTML = `<label>  Cache   path <input class="input-error" /></label>`;
    expect(findFirstInvalidField()?.label).toBe("Cache path");
    document.body.innerHTML = `<select class="select-error" aria-label="Streaming mode"></select>`;
    expect(findFirstInvalidField()?.label).toBe("Streaming mode");
    document.body.innerHTML = `<input aria-invalid="true" placeholder="Max GB" />`;
    expect(findFirstInvalidField()?.label).toBe("Max GB");
  });

  it("returns null when nothing invalid is rendered", () => {
    document.body.innerHTML = `<input class="input" />`;
    expect(findFirstInvalidField()).toBeNull();
    expect(revealFirstInvalidField()).toBe(false);
  });

  it("scrolls to and focuses the field", () => {
    document.body.innerHTML = `<label for="buf">Article Buffer Size</label><input id="buf" class="input-error" />`;
    const input = document.getElementById("buf")!;
    const scroll = vi.fn();
    input.scrollIntoView = scroll;
    expect(revealFirstInvalidField()).toBe(true);
    expect(scroll).toHaveBeenCalledWith({ behavior: "smooth", block: "center" });
    expect(document.activeElement).toBe(input);
  });
});
