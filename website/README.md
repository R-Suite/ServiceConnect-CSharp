# ServiceConnect Documentation Site

The source for [https://r-suite.github.io/ServiceConnect-CSharp/](https://r-suite.github.io/ServiceConnect-CSharp/).

Built with [Astro](https://astro.build) + [Starlight](https://starlight.astro.build) for the Learn / Samples / Releases tracks, and [DocFX](https://dotnet.github.io/docfx/) for the auto-generated API reference.

## Local development

Prerequisites: Node.js 20+, .NET SDK 8 and 10.

From the repository root:

```bash
# One-time: restore the pinned DocFX tool.
dotnet tool restore

# Build the .NET solution so XML doc files are emitted.
dotnet build src/ServiceConnect.sln --configuration Release

# Build the API reference into website/public/api/.
dotnet docfx website/docfx/docfx.json

# Install website dependencies.
cd website && npm install

# Run the dev server (http://localhost:4321/ServiceConnect-CSharp/).
npm run dev
```

The dev server hot-reloads Markdown changes in `src/content/docs/`. Changes to .NET source require re-running `dotnet build` and `dotnet docfx` to refresh the API reference.

## Deployment

Pushes to `master` that touch `website/**`, `src/**`, `filters/**`, or any `*.cs` / `*.csproj` file trigger `.github/workflows/docs.yml`, which rebuilds the site and deploys to GitHub Pages.

The workflow can also be run manually on any branch via the Actions tab ("Run workflow") to verify the build without deploying.

## One-time GitHub Pages setup

In the GitHub repo:

1. Go to **Settings → Pages**.
2. Set **Source** to **GitHub Actions**.
3. The first successful workflow run will publish the site at https://r-suite.github.io/ServiceConnect-CSharp/.
