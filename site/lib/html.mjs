// Minimal, dependency-free HTML templating.
// Interpolated values are escaped unless they were produced by `html` or `raw`.

const SAFE = Symbol("safe-html");

export const raw = (value) => ({ [SAFE]: true, value: String(value) });

export const isSafe = (value) => Boolean(value && value[SAFE]);

export const escapeHtml = (value) =>
  String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

const render = (value) =>
  value === null || value === undefined || value === false
    ? ""
    : Array.isArray(value)
      ? value.map(render).join("")
      : isSafe(value)
        ? value.value
        : escapeHtml(value);

export const html = (strings, ...values) =>
  raw(strings.reduce((out, part, index) => out + part + (index < values.length ? render(values[index]) : ""), ""));

export const toString = (fragment) => render(fragment);
