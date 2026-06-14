# ServiceConnect Documentation Site

The source for [https://r-suite.github.io/ServiceConnect-CSharp/](https://r-suite.github.io/ServiceConnect-CSharp/).

Built with [Astro](https://astro.build) + [Starlight](https://starlight.astro.build). The API reference under `/reference/` is hand-authored MDX.

## Local development

Prerequisites: Node.js 20+.

From this directory:

```bash
# Install dependencies.
npm ci

# Run the dev server (http://localhost:4321/ServiceConnect-CSharp/).
npm run dev
```

The dev server hot-reloads Markdown changes in `src/content/docs/`.

## Deployment

Pushes to `master` that touch `website/**` or `.github/workflows/docs.yml` trigger `.github/workflows/docs.yml`, which rebuilds the site and deploys to GitHub Pages.

The workflow can also be run manually on any branch via the Actions tab ("Run workflow") to verify the build without deploying.

## One-time GitHub Pages setup

In the GitHub repo:

1. Go to **Settings → Pages**.
2. Set **Source** to **GitHub Actions**.
3. The first successful workflow run will publish the site at https://r-suite.github.io/ServiceConnect-CSharp/.
