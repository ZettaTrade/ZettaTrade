# ZettaTrade Documentation Site

This is the documentation website for ZettaTrade, built with React, TypeScript, and Tailwind CSS.

## 🚀 Development

```bash
# Install dependencies
npm install

# Start dev server
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

## 📦 Deployment

The documentation is automatically deployed to GitHub Pages when changes are pushed to the main branch.

### Manual Deployment

```bash
npm run build
# Upload the contents of dist/ to your gh-pages branch
```

## 🏗️ Structure

```
docs-site/
├── src/
│   ├── App.tsx          # Main application component
│   ├── main.tsx         # Entry point
│   └── index.css        # Global styles
├── public/              # Static assets
├── index.html           # HTML template
├── package.json         # Dependencies
├── vite.config.ts       # Vite configuration
├── tsconfig.json        # TypeScript configuration
├── tailwind.config.js   # Tailwind CSS configuration
└── postcss.config.js    # PostCSS configuration
```

## 🎨 Features

- **Overview** - Project features and technology stack
- **Roadmap** - Development phases and timeline
- **Architecture** - System design and patterns
- **MCP Flow** - Model Context Protocol integration details
- **Settings & i18n** - Configuration and localization system
- **Structure** - Project file organization

## Landing page

A static product landing (en + ru) rides the docs-site deploy: the copy lives
under `docs-site/public/landing/` (`index.html` - English, `index.ru.html` -
Russian), and the screenshots and stylesheet under
`docs-site/public/assets/landing/`. Vite copies `public/` verbatim into
`dist/`, so the built site serves the page at `landing/` with every asset path
relative - no build config, no code changes needed. The screenshot shipped is
a real run capture (the demo workspace in the Dark theme); the
interactive-surface shots are a pending real-run capture gap and are not faked.
The download section links the GitHub Releases page: the delivery pipeline
(T-125) attaches the per-OS archives to every version-tagged release.

## Public metrics page

`public/metrics/index.html` is **generated, never hand-edited**: the alpha
program (T-129, `docs/ALPHA-PROGRAM.md`) renders it with
`scripts/generate-metrics.ps1` from the public GitHub REST API, and the
`metrics` workflow rebuilds it weekly. Every number carries the link that
re-derives it; local regeneration is one command —
`pwsh scripts/generate-metrics.ps1`.

## 📝 Note

This is a documentation-only site. The main application source code is in the `src/` directory at the repository root.
