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

## 📝 Note

This is a documentation-only site. The main application source code is in the `src/` directory at the repository root.
