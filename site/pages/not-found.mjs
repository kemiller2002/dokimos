import { html } from "../lib/html.mjs";
import { pageIntro } from "../lib/components.mjs";

export const page = {
  path: "/404.html",
  title: "Page not found",
  description: "The requested Dokimos page does not exist.",
  indexable: false,
};

export const render = ({ site }) => html`
${pageIntro({
  eyebrow: "404 · No evidence at this address",
  title: "This page does not exist.",
  lead: "The address may be mistyped, or the page may have moved. Dokimos would rather say so than show you something that is not there.",
  children: html`<div class="actions">
    <a class="button primary" href="/">Go to the Dokimos overview</a>
    ${site.navigation.map((item) => html`<a class="button" href="${item.path}">${item.label}</a>`)}
  </div>`,
})}`;
