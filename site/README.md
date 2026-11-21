# MiniCloud landing page

The site uses plain HTML and CSS. The build script stages the public files and
an explicit list of shared README assets. Asset links stay relative so they
work under the GitHub Pages project path `/minicloud/`.

## Preview locally

From the repository root, build to a new directory and serve its parent.

```sh
site/build.sh .local/artifacts/pages-preview/minicloud
python3 -m http.server 8248 --bind 127.0.0.1 --directory .local/artifacts/pages-preview
```

Open <http://127.0.0.1:8248/minicloud/>. Use a fresh output directory for each
build. The project marks come from the artwork sources credited in the
[root README](../README.md#open-source-components).

## Publish

Set **Settings → Pages → Source** to **GitHub Actions**, then run
**Publish landing page** on `main` from the Actions tab. The site is deployed to
<https://muniventures.github.io/minicloud/>.
