# ZettaTrade Documentation for GitHub Pages

This repository contains documentation for ZettaTrade - a cross-platform trading terminal with AI integration.

## 📚 Documentation Structure

The documentation includes:
- **Overview** - Project features and technology stack
- **Roadmap** - Development phases and timeline
- **Architecture** - System design and patterns
- **MCP Flow** - Model Context Protocol integration details
- **Settings & i18n** - Configuration and localization system
- **Structure** - Project file organization

## 🚀 Deploy to GitHub Pages

### Option 1: Using GitHub Actions (Recommended)

1. Create `.github/workflows/deploy.yml`:

```yaml
name: Deploy to GitHub Pages

on:
  push:
    branches: [main]

permissions:
  contents: read
  pages: write
  id-token: write

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup Node
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          
      - name: Install dependencies
        run: npm ci
        
      - name: Build
        run: npm run build
        
      - name: Setup Pages
        uses: actions/configure-pages@v4
        
      - name: Upload artifact
        uses: actions/upload-pages-artifact@v3
        with:
          path: dist
          
      - name: Deploy to GitHub Pages
        id: deployment
        uses: actions/deploy-pages@v4
```

2. Enable GitHub Pages in repository settings:
   - Go to Settings → Pages
   - Source: "GitHub Actions"

3. Push to main branch - documentation will auto-deploy

### Option 2: Manual Deploy

```bash
# Install dependencies
npm install

# Build for production
npm run build

# The dist/ folder contains the static site
# Upload it to your gh-pages branch or hosting service
```

## 📝 Using GitHub-Optimized Version

The `App.github.tsx` file is optimized for GitHub Pages:
- ❌ Removed download buttons (not needed for documentation)
- ❌ Removed Skill File tab (internal development tool)
- ✅ Kept all documentation tabs
- ✅ Added GitHub link in footer

To use it:
1. Rename `src/App.tsx` to `src/App.backup.tsx`
2. Rename `src/App.github.tsx` to `src/App.tsx`
3. Build and deploy

## 🔧 Development

```bash
# Start dev server
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

## 📄 License

MIT License - see LICENSE file for details

## 🔗 Links

- [ZettaTrade Repository](https://github.com/ZettaTrade/ZettaTrade)
- [Documentation](https://zettatrade.github.io/ZettaTrade/)
