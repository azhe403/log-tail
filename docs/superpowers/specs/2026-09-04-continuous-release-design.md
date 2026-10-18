# Continuous GitHub Releases for LogTail

**Date:** 2026-09-04
**Status:** Implemented
**Scope:** `.github/workflows/release.yml` (CI/CD only, no app code changes)

## Problem

LogTail currently has no automatic release mechanism. Every change on the main branch is only recorded in the commit history — there is no binary to download, no structured changelog, and no easy way for users to access the latest version without cloning and building it themselves. For an early-stage project that already has 2 feature commits, it is time to set up a minimal release pipeline so that every commit to main is automatically published as a usable artifact.

## Goal

Every push to the `main` branch automatically generates a new GitHub release with multi-platform self-contained binaries (Windows x64, Linux x64, macOS x64) as assets, and an auto-generated changelog from the commit history. Tag format: `YYYY.MM.DD-<short-sha>` (calver + 7-char SHA) — readable, sortable, collision-free.

## Design

### Files to Be Created

```
.github/
└── workflows/
    └── release.yml         # Single workflow file with 3 build jobs + 1 release job
```

No other files change. The app source code (Core, UI) is not touched.

### Workflow: `release.yml`

#### Trigger

```yaml
on:
  push:
    branches: [main]
  workflow_dispatch:        # Manual re-trigger if the previous workflow failed
```

Only pushes to `main` trigger the auto-release. `workflow_dispatch` was added to recover from a failed run without pushing a new commit. Pull requests to main **do not** trigger this workflow (not relevant for the "every commit = release" model).

#### Permissions

```yaml
permissions:
  contents: write
```

Minimal scope. `contents: write` is enough for `softprops/action-gh-release` to create the release and tag. There is no `packages:`, `issues:`, or other permission — the principle of least privilege.

#### Concurrency

```yaml
concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: true
```

If a new push to main arrives while the previous workflow is still running, the older one is canceled. This prevents workflow runs from piling up if several commits arrive in quick succession. The group key uses `github.ref` (that is, `refs/heads/main`), so every branch has its own concurrency group, but for this workflow only `main` triggers it.

#### Jobs

##### Jobs 1-3: Build matrix (parallel)

Three identical jobs except for the runner and RID, run in parallel via the matrix strategy:

```yaml
jobs:
  build:
    name: build-${{ matrix.os }}
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        include:
          - os: windows-latest
            rid: win-x64
            artifact_name: logtail-win-x64
          - os: ubuntu-latest
            rid: linux-x64
            artifact_name: logtail-linux-x64
          - os: macos-latest
            rid: osx-x64
            artifact_name: logtail-osx-x64
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore
        run: dotnet restore src/LogTail.UI/LogTail.UI.csproj
      - name: Publish
        run: >
          dotnet publish src/LogTail.UI/LogTail.UI.csproj
          -c Release
          -r ${{ matrix.rid }}
          --self-contained true
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -o ./publish/${{ matrix.rid }}
      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.artifact_name }}
          path: ./publish/${{ matrix.rid }}
```

**Matrix mapping:**

| `matrix.os`     | `matrix.rid`  | `matrix.artifact_name` | Asset filename   |
|-----------------|---------------|------------------------|------------------|
| `windows-latest` | `win-x64`    | `logtail-win-x64`      | `LogTail.exe`   |
| `ubuntu-latest`  | `linux-x64`  | `logtail-linux-x64`    | `LogTail`       |
| `macos-latest`   | `osx-x64`    | `logtail-osx-x64`      | `LogTail`       |

**Why `-p:IncludeNativeLibrariesForSelfExtract=true`:** Avalonia (and several .NET native deps) needs to extract native libs to a temp folder at runtime. Without this flag, the single-file binary can error at startup. This flag is documented in the official .NET docs for self-contained single-file deployments with native deps.

**Why `fetch-depth: 0`:** the default `actions/checkout@v4` shallow clone (depth 1). For changelog generation in the release job, we need the full commit history so that `git log` can list every commit since the last release.

##### Job 4: Release

```yaml
  release:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - name: Generate tag
        id: tag
        run: |
          DATE=$(date -u +'%Y.%m.%d')
          SHA=${GITHUB_SHA::7}
          TAG="${DATE}-${SHA}"
          echo "tag=${TAG}" >> $GITHUB_OUTPUT
      - name: Generate changelog
        run: |
          set -e
          PREVIOUS=$(git tag --list '[0-9][0-9][0-9][0-9].*' --sort=-v:refname | head -n 1 || echo "")
          {
            echo "## Changes"
            echo ""
            if [ -z "$PREVIOUS" ]; then
              # First release: show last 50 commits
              git log -n 50 --pretty=format:'- %h %s (%an)'
            else
              git log ${PREVIOUS}..${GITHUB_SHA} --pretty=format:'- %h %s (%an)'
            fi
            echo ""
            echo ""
            echo "---"
            echo "Built from commit \`${GITHUB_SHA}\` on \`$(date -u +'%Y-%m-%d %H:%M UTC')\`"
          } > CHANGELOG.md
      - name: Download all artifacts
        uses: actions/download-artifact@v4
        with:
          path: artifacts
      - name: Create GitHub release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ steps.tag.outputs.tag }}
          name: ${{ steps.tag.outputs.tag }}
          body_path: CHANGELOG.md
          fail_on_unmatched_files: true
          files: |
            artifacts/**/LogTail.exe
            artifacts/**/LogTail
```

**Tag generation:**

- `DATE=$(date -u +'%Y.%m.%d')` → `2026.09.04` (UTC, so the value is deterministic across runner timezones)
- `SHA=${GITHUB_SHA::7}` → a 7-char short SHA, sufficient for uniqueness at this project's scale (the collision probability is astronomically low for 7 hex chars = 268 million possibilities per date)
- Result: `2026.09.04-7fc2cd8`

**Changelog generation:**

- Find the previous tag with the pattern `[0-9][0-9][0-9][0-9].*` (any tag that starts with the 4-digit year) — this filters out semver tags such as `v1.0.0` if they appear later.
- Sort descending and take the top entry = the newest tag.
- If one exists: `git log <prev>..<current> --pretty=format:'- %h %s (%an)'` — lists the commits between the two tags.
- If none exists (first release): fall back to `git log -n 50` so the changelog is not empty.
- Append build metadata (commit SHA + timestamp) in the footer.

**Asset pattern matching:**

- `artifacts/**/LogTail.exe` → Windows binary
- `artifacts/**/LogTail` → Linux + macOS binary (same file name, different OS)
- `fail_on_unmatched_files: true` → the workflow fails if the pattern matches nothing. Failing loudly is better than publishing a release without binaries.

**`needs: build`:** the release job runs only if ALL matrix build jobs succeed. If any of them fail, no release is created. This follows the "skip release on build fail" decision.

### Error Handling

| Failure                                  | Behavior                                                                                          |
|------------------------------------------|---------------------------------------------------------------------------------------------------|
| Build job fails (one platform)    | The release job does not run. The next commit that fixes it triggers a new release.                     |
| Build succeeds, release job fails           | The workflow run fails. Re-trigger via `workflow_dispatch` (re-runs all jobs).                   |
| Tag collision (extremely unlikely)     | `softprops/action-gh-release@v2` fails with a clear error. The workflow run fails.                    |
| Push while the previous workflow is still running | The older workflow is canceled via `concurrency.cancel-in-progress`. Prevents runs from piling up.          |
| First release (no previous tag yet)  | The changelog falls back to `git log -n 50`. The tag is still created normally.                                   |

### Security

- **No secrets needed.** This workflow uses no external API key, deploys to no other service, and needs no OIDC.
- **Minimal `GITHUB_TOKEN` permissions** (`contents: write` only) — reduces the blast radius if the workflow is exploited.
- **Pinned action versions** — use `@v4` and `@v2` major version tags (they automatically get patch updates), or pin to a full SHA for maximum security. Default: the major tag. Can be tightened later.
- **Checkout uses the official action** (`actions/checkout@v4`) — not a third-party one.
- **No `pull_request_target` trigger** — avoids the fork code execution risk.
- **Build steps are deterministic** — no ad-hoc `apt install` or `brew install`; everything goes through the official toolchain.

## Out of Scope (YAGNI)

- **Code signing** for the Windows binary (users will see a SmartScreen warning; acceptable for the early stage)
- **Notarization** for the macOS binary (users need "Open Anyway" in Gatekeeper)
- **Auto-update mechanism** inside the app
- **NuGet publishing** (LogTail is an app, not a library)
- **Docker image** (not relevant for a desktop app)
- **Homebrew / Scoop / winget distribution** (high effort, not needed now)
- **Cosign / sigstore signing** for release artifacts
- **PR preview builds** — PRs do not trigger this workflow
- **`dotnet test` in CI** — deferred to a separate workflow or extended later
- **Self-update version in the binary** (no `<Version>` in Directory.Build.props) — nice-to-have, not required

## Risks

1. **CI cost goes up 3x** because of the 3-platform matrix. The `macos-latest` runner is 10x more expensive than Linux. Total estimate: ~$0.08/min × ~10 min × 3 jobs + ~$0.08/min × ~2 min release = ~$2.6 per push. Acceptable for an early-stage project. Future mitigation: conditional builds (skip a platform when the diff is docs-only).
2. **macOS binary without code signing/notarization** — users running it for the first time must right-click → Open. This will be documented in the README later (if one exists).
3. **The first-release changelog can be noisy** when the repo already has many commits without a release — the `git log -n 50` fallback mitigates this.
4. **Tags cannot be reverted** — if a commit turns out not to be releasable (e.g. a reverted commit), the tag is already created. Mitigation: manually `gh release delete` + re-tag via `workflow_dispatch` (but this workflow auto-generates the tag, so that is not straightforward — accepted as a limitation).
5. **Self-contained binaries are ~80-120MB per platform** — roughly ~300MB total per release on GH. Acceptable for now, and no problem with GH's retention policy (release assets are permanent).

## Testing Strategy

This workflow **is** the end-to-end test. Verify:

1. **Pre-push:** validate the YAML syntax (locally with `actionlint` or `act` in Docker — optional, not required for the first iteration).
2. **First push:** commit the workflow file → push to main → observe the workflow run in the GH Actions tab. It must:
   - 3 build jobs succeed (green checks)
   - 1 release job succeeds
   - a new release appears on the GH Releases page
   - the release has 3 binary assets (LogTail.exe, LogTail for Linux, LogTail for macOS)
   - the release body has the changelog
3. **Smoke test:** download the Windows binary from the release, run it locally, and verify that the app starts normally and can load a sample log file. (The Linux/macOS binaries are trusted by each user, or deferred for now.)
4. **Failure test:** push a commit that introduces a syntax error in the csproj → verify that 1+ build jobs fail → the release job does not run → no release is created → fix it and push again → a new release that skips the broken commit.

## Implementation Order

1. Create `.github/workflows/release.yml` with the full contents per the spec
2. Commit: `chore(ci): add continuous release workflow`
3. Push to main
4. Observe the first workflow run in GH Actions
5. Verify that the release appears on the GH Releases page
6. Download Windows binary, smoke test
7. (Optional) Set up `actionlint` locally for pre-commit validation of future workflow changes
8. (Optional) Add a README "Download" section that links to the latest release

## Notes

- `softprops/action-gh-release@v2` was chosen because: (a) it is widely used (~10k+ public repos), (b) it supports `body_path` for reading the changelog from a file, (c) it supports `fail_on_unmatched_files` for fail-loud behavior, and (d) it auto-creates the tag and release in one step.
- `actions/checkout@v4` and `actions/setup-dotnet@v4` are official Microsoft/GitHub actions.
- The matrix uses the `include` form (3 explicit entries with `os` + `rid` + `artifact_name`) rather than the shorthand array. This is more explicit — every entry is self-contained and does not rely on an index-based parallel array that is fragile when reordered.
