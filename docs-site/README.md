# ZettaTrade Documentation Site

The static documentation site of ZettaTrade, built with Vite, React,
TypeScript and Tailwind CSS. Deployed to GitHub Pages at
`https://zettatrade.github.io/ZettaTrade/` (the Vite base is
`/ZettaTrade/`, see `vite.config.ts`).

## What lives on the site

| Path | What it is | Where its truth comes from |
| --- | --- | --- |
| `/` | The site's home (this React app) | `src/App.tsx` |
| `/landing/` (+ `index.ru.html`) | The product landing, en and ru | committed static pages in `public/landing/` |
| `/metrics/` | The public metrics page | generated weekly by `metrics.yml` via `scripts/generate-metrics.ps1` (committed) |
| `/changelog/` | The rendered CHANGELOG | rendered at deploy time from `CHANGELOG.md` by `scripts/generate-site-pages.ps1` |
| `/manual/` | The rendered user manual | rendered at deploy time from `docs/USER-MANUAL.md` by `scripts/generate-site-pages.ps1` |

The landing's download section is stamped at deploy time by
`scripts/stamp-landing-downloads.ps1`: the marked links
(`data-zt-dl="..."`) are rewritten to the latest release's own per-OS
artifacts, each URL HEAD-verified to answer 200. Until the first version
tag the links stay on the Releases page — the committed state is
release-agnostic on purpose.

## Development

```bash
npm install

# Start dev server
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

The changelog and manual pages are not committed: render them before a
local preview with `pwsh scripts/generate-site-pages.ps1` (writes into
`public/`), then `npm run build`.

## Deployment

Automated by `.github/workflows/pages.yml`: every push to `main` that
touches the site, the two markdown sources or the deploy scripts runs the
full pipeline — build, generate, stamp, the strict local link gate
(`scripts/check-site-links.ps1`) — and publishes via
`actions/deploy-pages`. A failed gate leaves the previous deployment
standing. There is no gh-pages branch; the deployed artifact is the
workflow's own upload. Manual runs are possible through
`workflow_dispatch`.

## Structure

```
docs-site/
  index.html           # HTML template (the React entry, OG meta + favicon)
  src/
    App.tsx            # The site's home page (the entry door)
    main.tsx           # Entry point
    index.css          # Global styles
  public/              # Static assets, copied into dist/ verbatim
    landing/           # The en/ru product landing (the deploy stamps its downloads)
    metrics/           # The committed metrics page
    changelog/         # Generated at deploy time (not committed)
    manual/            # Generated at deploy time (not committed)
    favicon.ico        # The app's own icon
    zettatrade-mark.svg
  package.json
  vite.config.ts       # Vite configuration (base /ZettaTrade/)
  tailwind.config.js
  postcss.config.js
```
