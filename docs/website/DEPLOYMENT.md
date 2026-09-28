# Website deployment

## Pipeline

```
pull request ──► .github/workflows/site-validation.yml ──► site gate ──► dist/ uploaded as a review artifact (never deployed)

push to main / workflow_dispatch ──► .github/workflows/deploy-pages.yml
        build job:  clean checkout ──► site gate ──► configure-pages ──► upload-pages-artifact (dist/)
        deploy job: deploy-pages ──► environment github-pages
```

The site gate (`.github/actions/site-gate/action.yml`) is identical for both: `npm ci`, ROS registry check and validation, Visual Engineering init/no-drift/verify, `npm test`, clean `npm run build`, and the browser audit. Any failure stops the job before an artifact exists, so a failed gate cannot deploy. Deployment has `contents: read` only and cannot modify the repository.

Actions use the current stable major versions at the time of writing: `actions/checkout@v7`, `actions/setup-node@v7`, `actions/configure-pages@v6`, `actions/upload-pages-artifact@v5`, `actions/deploy-pages@v5`.

## One-time settings outside the repository

These cannot be performed from the repository and must be done by a repository or DNS administrator. Each item states how to verify it.

1. **Pages source.** GitHub → `kemiller2002/dokimos` → Settings → Pages → Build and deployment → Source: **GitHub Actions**.
   Verify: the first `Deploy site` run on `main` completes its `deploy` job.
2. **Custom domain.** Settings → Pages → Custom domain: `dokimos.echelonfoundry.com`, then save. (With Actions deployments the repository setting is authoritative; `dist/CNAME` is shipped as well so the declaration travels with the artifact.)
3. **DNS.** In the `echelonfoundry.com` zone add exactly one record, changing nothing else:

   | Name | Type | Value |
   | --- | --- | --- |
   | `dokimos` | `CNAME` | `kemiller2002.github.io.` |

   Verify: `dig +short dokimos.echelonfoundry.com CNAME` returns `kemiller2002.github.io.`
4. **Domain verification (recommended).** GitHub → account Settings → Pages → Add a verified domain `echelonfoundry.com` (TXT record `_github-pages-challenge-kemiller2002`), to prevent takeover of the subdomain.
5. **HTTPS.** After the certificate is issued (can take up to an hour), enable **Enforce HTTPS** in Settings → Pages.
6. **Environment protection (optional).** Settings → Environments → `github-pages`: restrict deployment branches to `main`.

## Post-deployment verification

Run after the first successful deployment and after any infrastructure change:

```bash
curl -sI https://dokimos.echelonfoundry.com/ | head -1                       # HTTP/2 200
curl -sI http://dokimos.echelonfoundry.com/ | grep -i location              # redirects to https
for p in / /how-it-works/ /quality-model/ /metrics/ /architecture/; do
  curl -s -o /dev/null -w "%{http_code} $p\n" "https://dokimos.echelonfoundry.com$p"
done                                                                        # all 200
curl -s -o /dev/null -w "%{http_code}\n" https://dokimos.echelonfoundry.com/metrics      # 301 to /metrics/
curl -s -o /dev/null -w "%{http_code}\n" https://dokimos.echelonfoundry.com/nope/        # 404
curl -s https://dokimos.echelonfoundry.com/ | grep -o 'dokimos.css?v=[a-f0-9]*'          # stylesheet link
curl -s https://dokimos.echelonfoundry.com/metrics/ | grep canonical        # https://dokimos.echelonfoundry.com/metrics/
curl -s https://dokimos.echelonfoundry.com/sitemap.xml | grep -c '<loc>'    # 5
```

Also open the site on a phone, confirm the stylesheet and fonts load, and run the browser audit against production:
the audit targets `dist/` locally, so compare the live HTML with the artifact of the same commit (`Built from <sha>` in the footer).

Record the result as a ROS work event or an entry under `observations/` with the date, commit, and each check's outcome. Do not record a check as passing unless it was run.
