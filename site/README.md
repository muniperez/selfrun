# Selfrun landing page

The site uses plain HTML and CSS targeting <https://selfrun.dev>. The build
script stages the public files and an explicit list of shared README assets.
Asset links stay relative (`./`) so they work both at the custom domain root
(`https://selfrun.dev/`) and under the GitHub Pages project path
(`/selfrun/`).

## Preview locally

From the repository root, build to a new directory and serve it.

```sh
site/build.sh .local/artifacts/pages-preview/selfrun
python3 -m http.server 8248 --bind 127.0.0.1 --directory .local/artifacts/pages-preview/selfrun
```

Open <http://127.0.0.1:8248/>. Use a fresh output directory for each build. The
project marks come from the artwork sources credited in the
[root README](../README.md#open-source-components).

## Publish

Set **Settings → Pages → Source** to **GitHub Actions**, then run
**Publish landing page** on `main` from the Actions tab. The workflow builds
and smoke-tests the CLI on each supported platform and stages its archive under
`downloads/`. The local HTML preview omits these archives.

Once DNS for `selfrun.dev` is pointed at GitHub Pages, configure the custom
domain in **Settings → Pages**.
