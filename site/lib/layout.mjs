// Page shell: document metadata, header, footer.

import { html, raw, toString } from "./html.mjs";

const absolute = (site, path) => new URL(path, site.origin).href;

// Header and footer use the Echelon Foundry markup so the shared stylesheet
// (assets/css/echelon-foundry.css) styles them exactly as on echelonfoundry.com.
const header = (site, currentPath) => html`
<header class="site-header">
  <div class="nav-shell">
    <div class="brand">
      <a href="/" class="brand-link" aria-label="Dokimos home"${currentPath === "/" ? raw(' aria-current="page"') : ""}>
        <span class="brand-mark" aria-hidden="true">Dk</span>
        <span>Dokimos</span>
      </a>
      <span class="brand-subtitle">Code-quality evidence · Echelon Foundry</span>
    </div>
    <nav aria-label="Primary">
      ${site.navigation.map(
        (item) => html`<a href="${item.path}"${item.path === currentPath ? raw(' aria-current="page"') : ""}>${item.label}</a>`,
      )}
      <a href="${site.repository}" class="pill-link">Source<span class="visually-hidden"> code on GitHub</span></a>
    </nav>
  </div>
</header>`;

const footer = (site, build) => html`
<footer class="site-footer">
  <div class="footer-grid">
    <div>
      <p class="eyebrow">Dokimos · An Echelon Foundry system</p>
      <p>Code-quality evidence across time, so teams can tell ordinary change from structural degradation.</p>
    </div>
    <div class="footer-actions">
      <a href="${site.repository}" class="button ghost">View Dokimos on GitHub</a>
      <a href="${site.parent.url}" class="muted-link">echelonfoundry.com</a>
    </div>
  </div>
  <p class="footer-note">&copy; ${build.year} Echelon Foundry <span>Built from <a href="${site.repository}/commit/${build.revision}">${build.revision.slice(0, 7)}</a></span></p>
</footer>`;

const structuredData = (site, page) =>
  JSON.stringify({
    "@context": "https://schema.org",
    "@type": "WebPage",
    name: page.title,
    description: page.description,
    url: absolute(site, page.path),
    isPartOf: { "@type": "WebSite", name: "Dokimos", url: absolute(site, "/") },
    about: {
      "@type": "SoftwareApplication",
      name: "Dokimos",
      applicationCategory: "DeveloperApplication",
      codeRepository: site.repository,
      publisher: { "@type": "Organization", name: site.parent.name, url: site.parent.url },
    },
  }).replaceAll("<", "\\u003c");

export const documentTitle = (page) => (page.path === "/" ? `${page.title} | Echelon Foundry` : `${page.title} | Dokimos · Echelon Foundry`);

export const renderDocument = ({ site, build, assets, page, content }) =>
  `<!DOCTYPE html>\n${toString(html`<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${documentTitle(page)}</title>
  <meta name="description" content="${page.description}">
  <link rel="canonical" href="${absolute(site, page.path)}">
  ${page.indexable === false ? html`<meta name="robots" content="noindex">` : ""}
  <meta name="theme-color" content="#202421">
  <meta property="og:type" content="website">
  <meta property="og:site_name" content="Dokimos · Echelon Foundry">
  <meta property="og:locale" content="${site.locale}">
  <meta property="og:title" content="${page.title}">
  <meta property="og:description" content="${page.description}">
  <meta property="og:url" content="${absolute(site, page.path)}">
  <meta property="og:image" content="${absolute(site, assets.socialImage)}">
  <meta property="og:image:alt" content="Dokimos: a complexity trajectory showing baseline, best demonstrated state, and a regression.">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="twitter:title" content="${page.title}">
  <meta name="twitter:description" content="${page.description}">
  <meta name="twitter:image" content="${absolute(site, assets.socialImage)}">
  <link rel="icon" href="${assets.favicon}" type="image/svg+xml">
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&family=Manrope:wght@400;500;600;700&family=Newsreader:opsz,wght@6..72,500;6..72,650&display=swap">
  ${assets.stylesheets.map((href) => html`<link rel="stylesheet" href="${href}">`)}
  <script type="application/ld+json">${raw(structuredData(site, page))}</script>
</head>
<body>
  <a class="skip-link" href="#main-content">Skip to main content</a>
  ${header(site, page.path)}
  <main id="main-content" tabindex="-1">
${content}
  </main>
  ${footer(site, build)}
</body>
</html>`)}\n`;
