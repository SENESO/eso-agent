# EsoAgent — Launch Report (INTERNAL — never pushed)

Launch lane run 2026-11-18. **Status: local preparation COMPLETE and
verified; all GitHub-side writes BLOCKED on an approval that timed out —
nothing was pushed, no repo was created.** Details and the exact resume
sequence are below.

## 1. Release key safety — DONE

- `.release-key/release-private.pem` was MOVED out of the tree to
  `~/.eso-secrets/release-private.pem` (dir 700, file 600); the
  `.release-key/` directory no longer exists in the tree.
- Verified cryptographically (no key material printed anywhere): the
  moved private key's derived public key is byte-identical to the
  product's pinned `PinnedPublicKeySpki` in
  `src/EsoAgent.Core/ReleaseChannel.cs`.
- The GitHub Actions secret `ESO_RELEASE_PRIVATE_KEY` could NOT be set:
  the connector's tool catalog has no secrets API, `gh` is not logged
  in, and no REST token exists on this machine.
  **Owner step (one paste):** repo Settings → Secrets and variables →
  Actions → New repository secret, name `ESO_RELEASE_PRIVATE_KEY`,
  value = the contents of `~/.eso-secrets/release-private.pem`.
  Until then the release workflow's signing step stays inert and the
  locally signed manifest (§3) is the v1.2.0 signing story — exactly as
  the lane brief prescribes.

## 2. Public tree preparation — DONE

- `.gitignore` written: `bin/`, `obj/`, `dist/`, `.release-key/`,
  `.smoke/`, `*.bak`, `*.orig`, editor/OS noise.
- `LICENSE` created: MIT, © 2026 Eslam Ashraf.
- `CHANGELOG.md` authored (public, no internal detail): 1.0.0 → 1.1.0 →
  1.2.0 from the version history.
- Include set (210 files, ~2.4 MB excluding bin/obj): `src/`, `tests/`,
  `scripts/`, `site/`, `.github/`, `install/`, `installer/`, `assets/`,
  `EsoAgent.sln`, `Directory.Build.props`, `README.md`, `USER-GUIDE.md`,
  `BENCHMARKS.md`, `ROADMAP.md`, `FEATURE-MATRIX.md`, `CODE_SIGNING.md`,
  `LICENSE`, `CHANGELOG.md`, `.gitignore`.
- Exclusions honored (never in the push set): `BUILD-REPORT.md`,
  `APP-REPORT.md`, `V11-REPORT.md`, `V12-REPORT.md`, this report,
  `.smoke/`, `dist/`, `brand/` (AI media-generation candidates +
  metadata — internal), all screenshots (`app-live-2026-11-18.png`,
  `app-relay-fixed-2026-11-18.webp`, `h-consent-prompt-2026-11-18.jpg`,
  `h-privacy-on-2026-11-18.jpg`), all bin/obj output.
- Secret sweep of the include set: NO credentials, tokens, owner
  passwords, private keys, cert fingerprints, box ids, or local paths.
  Only hits: the words "Simular cocoon" in three code/test comments and
  BENCHMARKS.md's honest benchmark provenance line ("Live run — Simular
  box main") — test-environment mentions, no internals. Left as-is so
  the public tree stays byte-identical to the live-verified source.
- Binary assets in the include set: 41 PNG/ICO files (~380 KB total —
  brand PNG set, site images, tray/app icons). The connector's
  snapshot push (`github push-files`) validates UTF-8 text only
  (≤2,000 files / ≤16 MiB — the ~169 text files fit easily), so the
  binaries need the empirical `push_files` test or a follow-up route;
  NOT yet attempted (blocked upstream by §4).

## 3. dist rebuilt post-fix + manifest verified — DONE

`scripts/publish.sh` re-run with `ESO_RELEASE_KEY_PATH` pointing at the
moved key. Output in `dist/`:

- `esoagent-windows.zip` (119,440,752 B), `esoagent-linux.zip`
  (41,275,811 B), `esoagent-server.zip` (83,439,201 B),
  `esoagent-client.zip` (83,413,930 B), `esoagent-app.zip`
  (109,982,197 B)
- `esoagent_1.2.0_amd64.deb` (32,815,672 B),
  `esoagent-server_1.2.0_amd64.deb` (32,871,208 B)
- `manifest.json` — SIGNED. Verified firsthand: all 6 artifact SHA-256
  hashes match the dist files, and the ECDSA P-256 signature verifies
  ("Verified OK") against the pinned product public key over the
  product's canonical form.

## 4. GitHub writes — NOT PERFORMED (approval timed out)

The first write, `create_repository` for public `SENESO/eso-agent` via
the GitHub connector, returned `approval_expired` ("egress approval
timed out for this invocation. The action was not performed."). Per
standing instructions the unapproved write was NOT retried. Everything
downstream is therefore still pending:

- Repo `SENESO/eso-agent`: not created. Push: not started (0 files).
- Tag `v1.2.0` / Release: not created. NOTE on channels, established
  firsthand this run: the connector catalog has NO tag, release,
  workflow-dispatch, Pages-settings, or secrets tools; the SSH key in
  `/root/.ssh/config` authenticates as a **deploy key scoped to
  SENESO/mandarin only**; `gh` is not logged in. So even after repo
  creation + `push-files` land the tree, the tag/Release will most
  likely need ONE owner action — Actions tab → `release` workflow →
  Run workflow with version `1.2.0` (the workflow builds everything and
  creates the Release itself), or a local `git tag v1.2.0 && git push`.
- Pages (eso-agent): the `pages` workflow triggers on the first push to
  main (site/** paths) and `actions/configure-pages` may auto-enable;
  the Settings → Pages → Source: GitHub Actions enablement has no API
  channel here if it does not. Unverified until the push happens.
- Profile hub `SENESO/seneso.github.io`: source ready at
  `~/workspace/profile-site/` (9 files to push: index.html, styles.css,
  app.js, data/stats.json + stats.js, scripts/update_stats.py,
  assets/cv.pdf [binary], .github/workflows/update-stats.yml, README;
  BUILD-NOTES.md excluded as internal). stats.json is real (generated
  2026-11-18). Its `STATS_TOKEN` secret is the same owner-paste class
  as §1 — without it the workflow falls back to public data only; the
  site is complete at launch either way.
- Live verification (step 7): nothing to verify yet — no URL exists.

## 5. Resume sequence (when the approval is granted)

1. `create_repository` (public, `eso-agent`) via the connector.
2. `github push-files` the ~169 text files to `main` (NUL-delimited
   manifest per the connector's snapshot-push procedure; record the
   sync entry in `~/workspace/eso-agent.muse-github-snapshot-sync.json`).
3. Test ONE small binary (`assets/brand/png/mark-16.png`, 557 B) via
   `push_files`; read back and hash-compare. If binaries store
   correctly, push the remaining 40 the same way; if not, record the
   binary set as a follow-up (PAT + HTTPS git push) — do not corrupt
   them through a text path.
4. Push the profile-site repo the same way; verify
   https://seneso.github.io/ and https://seneso.github.io/eso-agent/
   return 200 with expected content.
5. Owner actions (no channel here): paste the two Actions secrets
   (§1 + STATS_TOKEN); run the `release` workflow once with version
   1.2.0 (or push tag `v1.2.0`); enable Pages source = GitHub Actions
   if the workflow did not auto-enable it.
6. Verify the Release page lists the workflow-built assets (5 zips,
   2 debs, EsoAgent-Setup-*.exe, manifest.json) and one asset downloads.

ESO (laptop) and the Simular box deployment were not touched.

## 6. Resume attempt 2026-11-18 ~18:35 — approval granted, GitHub App cannot create repos

The owner approved continuing and was present. The resume ran:

- **`create_repository` (public `eso-agent`) — approval GRANTED this
  time, but the GitHub API returned `403 Resource not accessible by
  integration`** (`POST https://api.github.com/user/repos`). The Muse
  GitHub App installation does not hold repository-creation
  permission. This is a hard channel limit, not an approval problem;
  retrying cannot change it. The repo was NOT created. Creation of
  `SENESO/seneso.github.io` was not attempted — the limit is
  account-level, so it would spend another approval on a certain 403.
- **Nothing was pushed** (no target repo exists). All prepared state
  from §§1–3 is intact and unchanged.
- **Binary route — PROVEN nonexistent in this connector** (the §5
  step-3 test, run against local validation):
  `github push-files` on a 1-file manifest
  (`assets/brand/png/mark-16.png`, 557 B) rejected it locally:
  "snapshot file … is not UTF-8". The call-tool file tools
  (`push_files`, `create_or_update_file`) accept string content only —
  the server base64-encodes the string as given — so binary bytes
  cannot pass through them either. **The 41 PNG/ICO assets
  (490,455 B total) are a confirmed gap** until a binary-capable route
  exists (owner-side `git push` from any checkout, or a future
  connector capability). Exact list:
  `assets/brand/png/` — app-icon-128.png, app-icon-16.png,
  app-icon-24.png, app-icon-256.png, app-icon-32.png, app-icon-48.png,
  app-icon-512.png, app-icon-64.png, app-icon.ico, app-icon.png,
  logo-lockup-dark.png, logo-lockup-light.png, mark-128.png,
  mark-16.png, mark-24.png, mark-256.png, mark-32.png, mark-48.png,
  mark-512.png, mark-64.png, tray-controlled-48.png,
  tray-controlled-pulse.ico, tray-controlled.ico, tray-locked-48.png,
  tray-locked.ico, tray-offline-48.png, tray-offline.ico,
  tray-online-48.png, tray-online.ico (29 files);
  `site/assets/` — app-icon-256.png, logo-lockup-dark.png,
  mark-256.png (3);
  `src/EsoAgent.App/Assets/` — app-icon.png, tray-controlled.png,
  tray-offline.png, tray-online.png (4);
  `src/EsoAgent.Tray/Assets/` — tray-controlled-pulse.ico,
  tray-controlled.ico, tray-locked.ico, tray-offline.ico,
  tray-online.ico (5).
  Same class: the profile site's `assets/cv.pdf` (48 KB) is binary
  and cannot be pushed through this connector.
- **Text snapshot is ready to fire the moment the repos exist**:
  re-enumerated include set = 221 files (the §2 count of 210 was the
  earlier lane's tally; the explicit re-walk found 221 — 180 text
  files, 1,640,484 B of UTF-8, well inside the 2,000-file / 16 MiB
  snapshot limits; the 41-binary count matches §2 exactly). Include
  rules: dirs `src/ tests/ scripts/ site/ .github/ install/
  installer/ assets/` (skipping `bin/`, `obj/`, `node_modules/`) +
  root files `EsoAgent.sln`, `Directory.Build.props`, `README.md`,
  `USER-GUIDE.md`, `BENCHMARKS.md`, `ROADMAP.md`, `FEATURE-MATRIX.md`,
  `CODE_SIGNING.md`, `LICENSE`, `CHANGELOG.md`, `.gitignore`.
  Profile site text set (8 files): index.html, styles.css, app.js,
  data/stats.json, data/stats.js, scripts/update_stats.py,
  .github/workflows/update-stats.yml, README.md.

### What unblocks the launch (owner / browser-session actions)

1. **Create the two repos** (signed in as SENESO, github.com/new):
   `eso-agent` and `seneso.github.io` — both **Public**, both
   **completely empty** (no README, no .gitignore, no license
   template). Then confirm the **Muse GitHub App is installed on
   both repos**: github.com → Settings → Installations → Muse →
   Repository access — add both if the installation is set to
   selected repositories only. (Connector access is repo-scoped; a
   repo the App cannot reach cannot be pushed to.)
2. After that, the connector push of both text snapshots runs
   immediately (one approval each), and live verification follows.

### Owner one-time actions after the push (exact paths)

- **Secret `ESO_RELEASE_PRIVATE_KEY`** on `SENESO/eso-agent`:
  repo page → **Settings → Secrets and variables → Actions → New
  repository secret** → Name `ESO_RELEASE_PRIVATE_KEY` → Secret =
  the contents of `~/.eso-secrets/release-private.pem` (the file on
  the Muse VM; its contents are never transmitted through chat) →
  Add secret. Until set, the release workflow's signing step stays
  inert and the locally signed dist manifest (§3) is the v1.2.0
  signing story.
- **Secret `STATS_TOKEN`** on `SENESO/seneso.github.io`: same path
  (Settings → Secrets and variables → Actions → New repository
  secret), Name `STATS_TOKEN`, value = a personal access token the
  owner creates at github.com → Settings → Developer settings →
  Personal access tokens. Without it the stats workflow falls back
  to public data only; the committed stats.json is already real.
- **Trigger the Release**: `SENESO/eso-agent` → **Actions** tab →
  `release` workflow → **Run workflow** → version input `1.2.0` →
  Run workflow. The workflow builds the full artifact set and
  creates the GitHub Release itself.
- **Pages source** (both repos, only if the workflows did not
  auto-enable it): repo → **Settings → Pages** → Build and
  deployment → Source: **GitHub Actions**.

ESO (laptop) and the Simular box deployment were not touched.

## 7. Push attempt 2026-11-18 ~18:37 — approval granted, App lacks Contents write (403)

The two repos had been created (owner, via browser) and the Muse
GitHub App's repository access covered them. This run:

- **Pre-flight verified**: `list_commits` on both repos returned the
  explicit `409 Git Repository is empty` result; no prior sync records
  exist for either source dir (fresh first uploads). Manifests were
  rebuilt from the §6 include rules and validated exactly: eso-agent
  walk = 221 files; the 41 excluded binaries are byte-for-byte the §6
  list (same paths); text snapshot = **180 files, 1,640,484 B**, all
  valid UTF-8, no symlinks. Profile snapshot = the §6 8 text files
  (115,625 B), all UTF-8; local `data/stats.json` confirmed to carry
  ivmp-platform `treeLines: 1340188` (codeLines 384,861).
- **eso-agent push FIRED and FAILED at the API**: the connector write
  approval was **granted** by the owner, then `push_files` returned
  `failed to create tree: POST https://api.github.com/repos/SENESO/eso-agent/git/trees: 403 Resource not accessible by integration`.
  **Zero snapshot files were committed.** The Muse GitHub App
  installation does not hold Contents **write** permission — the
  "All repositories" setting verified in the browser is repository
  *selection*, not the permission grant; reads work, git writes do
  not. This is the same integration-permission wall as §6's
  `create_repository` 403, one level deeper. No connector route can
  bypass it (`create_or_update_file` uses the same App token;
  `gh`/SSH channels do not exist for these repos).
- **Provider side effect (inspected, harmless)**: during the attempt
  the provider initialized `SENESO/eso-agent` exactly as the
  snapshot-push procedure warns: one commit `91852a0c2783c38d9c6c33a1ed05f44c1ffcf364`
  ("Initial commit", 2026-11-18T16:37:03Z) containing a single
  **empty** `README.md` (0 B, blob `e69de29`). The repo is therefore
  NO LONGER empty. The snapshot's own `README.md` is a selected path
  and replaces the generated file on the successful retry; the retry
  must use the existing-repo path with recorded head
  `91852a0c2783c38d9c6c33a1ed05f44c1ffcf364` and treat that empty
  README as the only divergence (selected for replacement). The
  initial commit was NOT recorded as a snapshot sync — no sync
  record file was created or advanced for either repo.
- **Profile push NOT attempted**: same installation-level wall —
  firing it would spend another owner approval on a certain 403 and
  would initialize `seneso.github.io` the same way. Rechecked after
  the eso-agent attempt: still `409 Git Repository is empty`,
  untouched.
- **Live verification (step 3 of the brief)**: not applicable — no
  snapshot content reached GitHub. Verified instead: eso-agent tree =
  1 generated empty README only; profile repo = empty.

### The one unblock (owner, in the browser)

github.com → **Settings → Installations → Muse** → **Permissions**:
grant/accept **Contents: Read and write** for the installation (if a
banner offers a permission update, accept it). Repository access
("All repositories") is already correct and is a separate setting.
Once Contents write is granted, re-run this exact push phase — the
manifests are reproducible from §6 + the path list below (they were
also left at `/tmp/eso-agent-paths.nul` and `/tmp/profile-paths.nul`,
ephemeral). After a successful push, record the sync entries in
`~/workspace/eso-agent.muse-github-snapshot-sync.json` and
`~/workspace/profile-site.muse-github-snapshot-sync.json` with the
confirmed commit SHAs, then run the live verification (tree counts +
spot-checks incl. the stats.json `treeLines` value).

### Remaining after the push (unchanged from §6)

- 41 binary PNG/ICO files (490,455 B) + profile `assets/cv.pdf` —
  browser-side step (this connector is UTF-8-only, proven §6).
- Owner secrets: `ESO_RELEASE_PRIVATE_KEY` (eso-agent),
  `STATS_TOKEN` (seneso.github.io) — exact paths in §6.
- Trigger the `release` workflow once with version `1.2.0`
  (Actions tab) — creates Release v1.2.0 with the full asset set.
- Pages source = GitHub Actions on both repos if not auto-enabled.

### eso-agent snapshot paths (180, exact retry set)

```text
.github/workflows/pages.yml
.github/workflows/release.yml
.gitignore
BENCHMARKS.md
CHANGELOG.md
CODE_SIGNING.md
Directory.Build.props
EsoAgent.sln
FEATURE-MATRIX.md
LICENSE
README.md
ROADMAP.md
USER-GUIDE.md
assets/brand/README.md
assets/brand/svg/logo-lockup-dark.svg
assets/brand/svg/logo-lockup-light.svg
assets/brand/svg/mark-tile.svg
assets/brand/svg/mark.svg
install/install.ps1
install/install.sh
install/uninstall.ps1
install/uninstall.sh
install/update-apply.ps1
install/update-apply.sh
installer/windows/EsoAgent.iss
installer/windows/verify-install.ps1
scripts/make-manifest.py
scripts/package-deb.sh
scripts/publish.ps1
scripts/publish.sh
scripts/render-brand.py
site/index.html
src/EsoAgent.App/App.axaml
src/EsoAgent.App/App.axaml.cs
src/EsoAgent.App/AppConfig.cs
src/EsoAgent.App/AppTray.cs
src/EsoAgent.App/DeviceItem.cs
src/EsoAgent.App/EsoAgent.App.csproj
src/EsoAgent.App/MainWindow.axaml
src/EsoAgent.App/MainWindow.axaml.cs
src/EsoAgent.App/Program.cs
src/EsoAgent.App/Services/DesktopClient.cs
src/EsoAgent.App/Services/ExecStreamClient.cs
src/EsoAgent.App/Strings.cs
src/EsoAgent.App/Views/AccessView.axaml
src/EsoAgent.App/Views/AccessView.axaml.cs
src/EsoAgent.App/Views/ActivityView.axaml
src/EsoAgent.App/Views/ActivityView.axaml.cs
src/EsoAgent.App/Views/DesktopView.axaml
src/EsoAgent.App/Views/DesktopView.axaml.cs
src/EsoAgent.App/Views/FilesView.axaml
src/EsoAgent.App/Views/FilesView.axaml.cs
src/EsoAgent.App/Views/SettingsWindow.axaml
src/EsoAgent.App/Views/SettingsWindow.axaml.cs
src/EsoAgent.App/Views/StatusView.axaml
src/EsoAgent.App/Views/StatusView.axaml.cs
src/EsoAgent.App/Views/TerminalView.axaml
src/EsoAgent.App/Views/TerminalView.axaml.cs
src/EsoAgent.App/app.manifest
src/EsoAgent.Client/ApiClient.cs
src/EsoAgent.Client/Bench.cs
src/EsoAgent.Client/ClientConfig.cs
src/EsoAgent.Client/DeviceCommands.cs
src/EsoAgent.Client/DeviceCommandsV12.cs
src/EsoAgent.Client/EsoAgent.Client.csproj
src/EsoAgent.Client/FleetApiClient.cs
src/EsoAgent.Client/FleetCommands.cs
src/EsoAgent.Client/FleetCommandsV12.cs
src/EsoAgent.Client/Program.cs
src/EsoAgent.Client/RelayDesktopClient.cs
src/EsoAgent.Client/Tunnel.cs
src/EsoAgent.Core/Abstractions.cs
src/EsoAgent.Core/AccessGrants.cs
src/EsoAgent.Core/AgentConfig.cs
src/EsoAgent.Core/AuditLog.cs
src/EsoAgent.Core/Dashboard.cs
src/EsoAgent.Core/Dashboard.html
src/EsoAgent.Core/DashboardLogin.html
src/EsoAgent.Core/DesktopProtocol.cs
src/EsoAgent.Core/DesktopStreamRunner.cs
src/EsoAgent.Core/DeviceIdentity.cs
src/EsoAgent.Core/Doctor.cs
src/EsoAgent.Core/Efficiency.cs
src/EsoAgent.Core/Endpoints.cs
src/EsoAgent.Core/EnvAudit.cs
src/EsoAgent.Core/EsoAgent.Core.csproj
src/EsoAgent.Core/EsoAgentServer.cs
src/EsoAgent.Core/FileTransfer.cs
src/EsoAgent.Core/FleetClientService.cs
src/EsoAgent.Core/FleetProtocol.cs
src/EsoAgent.Core/FramePipeline.cs
src/EsoAgent.Core/HmacAuth.cs
src/EsoAgent.Core/Inventory.cs
src/EsoAgent.Core/Jobs.cs
src/EsoAgent.Core/LogFollow.cs
src/EsoAgent.Core/MeshApplier.cs
src/EsoAgent.Core/MeshProtocol.cs
src/EsoAgent.Core/MeshServices.cs
src/EsoAgent.Core/NetAndCert.cs
src/EsoAgent.Core/OperatorEndpoints.cs
src/EsoAgent.Core/OperatorPack.cs
src/EsoAgent.Core/OperatorPack2.cs
src/EsoAgent.Core/OwnerAuth.cs
src/EsoAgent.Core/Parsers.cs
src/EsoAgent.Core/PatchService.cs
src/EsoAgent.Core/PerceiveScripts/blender_perceive.py
src/EsoAgent.Core/PerceiveScripts/godot_perceive.gd
src/EsoAgent.Core/PerceiveScripts/unreal_perceive.py
src/EsoAgent.Core/PerceptionStack.cs
src/EsoAgent.Core/ProcessRunner.cs
src/EsoAgent.Core/ProductInfo.cs
src/EsoAgent.Core/Protocol.cs
src/EsoAgent.Core/QueueProtocol.cs
src/EsoAgent.Core/RelayShim.cs
src/EsoAgent.Core/ReleaseChannel.cs
src/EsoAgent.Core/ScheduleService.cs
src/EsoAgent.Core/ShellSessions.cs
src/EsoAgent.Core/Supervision.cs
src/EsoAgent.Core/Totp.cs
src/EsoAgent.Core/UpdateService.cs
src/EsoAgent.Core/V12.cs
src/EsoAgent.Core/VisionWindows.cs
src/EsoAgent.Core/WakeOnLan.cs
src/EsoAgent.Core/WgCrypto.cs
src/EsoAgent.Linux/EsoAgent.Linux.csproj
src/EsoAgent.Linux/LinuxDesktop.cs
src/EsoAgent.Linux/LinuxHelpers.cs
src/EsoAgent.Linux/LinuxInteractiveExec.cs
src/EsoAgent.Linux/LinuxPackageManager.cs
src/EsoAgent.Linux/LinuxPowerControl.cs
src/EsoAgent.Linux/LinuxRepair.cs
src/EsoAgent.Linux/LinuxScreenshot.cs
src/EsoAgent.Linux/LinuxServiceControl.cs
src/EsoAgent.Linux/LinuxSystemInfo.cs
src/EsoAgent.Linux/Program.cs
src/EsoAgent.Server/Access.cs
src/EsoAgent.Server/Activity.cs
src/EsoAgent.Server/CommandQueue.cs
src/EsoAgent.Server/Connect.cs
src/EsoAgent.Server/EsoAgent.Server.csproj
src/EsoAgent.Server/Fleet.html
src/EsoAgent.Server/FleetLogin.html
src/EsoAgent.Server/Health.cs
src/EsoAgent.Server/MeshHub.cs
src/EsoAgent.Server/OperatorApi.cs
src/EsoAgent.Server/OperatorApiV12.cs
src/EsoAgent.Server/Panel.cs
src/EsoAgent.Server/PanelV12.cs
src/EsoAgent.Server/Program.cs
src/EsoAgent.Server/Registry.cs
src/EsoAgent.Server/Streams.cs
src/EsoAgent.Server/V12Stores.cs
src/EsoAgent.Tray/DesktopSessionHandler.cs
src/EsoAgent.Tray/DxgiCapture.cs
src/EsoAgent.Tray/EsoAgent.Tray.csproj
src/EsoAgent.Tray/Program.cs
src/EsoAgent.Tray/TrayContext.cs
src/EsoAgent.Windows/ControlPipeService.cs
src/EsoAgent.Windows/DesktopBridge.cs
src/EsoAgent.Windows/EsoAgent.Windows.csproj
src/EsoAgent.Windows/Program.cs
src/EsoAgent.Windows/TrayPipeForwarder.cs
src/EsoAgent.Windows/WindowsPackageManager.cs
src/EsoAgent.Windows/WindowsPowerControl.cs
src/EsoAgent.Windows/WindowsRegistryAccess.cs
src/EsoAgent.Windows/WindowsRepair.cs
src/EsoAgent.Windows/WindowsServiceControl.cs
src/EsoAgent.Windows/WindowsSystemInfo.cs
tests/EsoAgent.Tests/AppBridgePresetTests.cs
tests/EsoAgent.Tests/EsoAgent.Tests.csproj
tests/EsoAgent.Tests/FileTransferTests.cs
tests/EsoAgent.Tests/FleetTests.cs
tests/EsoAgent.Tests/LinuxDesktopHelperTests.cs
tests/EsoAgent.Tests/MeshTests.cs
tests/EsoAgent.Tests/PackageUploadLimitTests.cs
tests/EsoAgent.Tests/ParserTests.cs
tests/EsoAgent.Tests/RelayDesktopTests.cs
tests/EsoAgent.Tests/SecurityTests.cs
tests/EsoAgent.Tests/V11Tests.cs
tests/EsoAgent.Tests/V12Tests.cs
```

### profile-site snapshot paths (8, exact retry set)

```text
index.html
styles.css
app.js
data/stats.json
data/stats.js
scripts/update_stats.py
.github/workflows/update-stats.yml
README.md
```

ESO (laptop) and the Simular box deployment were not touched. No
secret material appears in this section.

## 8. Push retry 2026-11-18 ~18:40 — push_files 403s on /git/trees, but Contents-API write WORKS

New evidence going in (browser, firsthand): the Meta Muse AI
installation page for SENESO lists Permissions = "Read access to
metadata" + **"Read and write access to code, issues, and pull
requests"**, with no permission-update request pending. This run
tested the hypothesis that §7's 403 came from the empty-repo path
rather than a missing grant.

- **Pre-flight**: manifests intact at `/tmp/eso-agent-paths.nul`
  (180 paths, README.md selected) and `/tmp/profile-paths.nul`
  (8 paths). `list_commits` confirmed eso-agent `main` head =
  `91852a0c2783c38d9c6c33a1ed05f44c1ffcf364` (single "Initial commit",
  empty README) and seneso.github.io still `409 Git Repository is
  empty`. No sync records exist for either source dir.
- **Step 1 — push_files retry (existing-repo path): FAILED with the
  identical 403**: approval granted, then
  `failed to create tree: POST https://api.github.com/repos/SENESO/eso-agent/git/trees: 403 Resource not accessible by integration`.
  Zero snapshot files committed. The empty-repo hypothesis is
  therefore **refuted**: the repo had a head commit this time and
  the tree endpoint still refused.
- **Step 2 — minimal write probe: SUCCEEDED.** `create_or_update_file`
  created `.launch-probe` (13 B) on eso-agent `main` — commit
  `0635fba42e6dcee1cc27395901e4e56cb00ace31`, blob
  `f4960e2bd35120f49ea03f0f959aee4030a0b1bb`. The probe was then
  deleted via `delete_file` — commit
  `a86a43a722ba138d17a0aa5174a9dc9849e8900c`. **Conclusion: the App
  token CAN write repository contents (Contents API: PUT/DELETE
  /contents), but CANNOT use the Git Data API tree endpoint
  (POST /git/trees) that `push_files` is built on.** §7's diagnosis
  ("installation lacks Contents write") was too broad — the block is
  specific to the git-trees path, not a total write block, and it
  persists despite the installation page showing code read+write.
- **Step 3 — profile push: NOT attempted.** The brief gated it on
  the eso-agent push-files success, which did not occur.
  seneso.github.io remains empty and untouched.
- **Step 4 — live state verified**: eso-agent tree at head
  `a86a43a722ba138d17a0aa5174a9dc9849e8900c` contains exactly one
  file — the provider's empty `README.md` (0 B, blob `e69de29`).
  Commit chain: `91852a0c` (Initial) → `0635fba4` (probe) →
  `a86a43a7` (probe removed). The probe add/delete pair is the only
  residue of this run; content-wise the repo is as §7 left it. No
  sync records were created or advanced (no confirmed snapshot).
  The stats.json `treeLines: 1340188` spot-check remains local-only
  (nothing of the snapshot is on GitHub yet).

### The route that remains

`push_files` is dead for this installation (two identical 403s on
/git/trees, empty-repo and existing-repo alike). Contents-API
writes demonstrably work, so the snapshot CAN be landed file-by-file
via `create_or_update_file` (180 calls for eso-agent, 8 for the
profile site — each a separate approved write, and files >128 KB
hit the shell-arg limit noted in §2/AGENTS.md, so the largest files
need a size check first), OR the owner grants/adjusts whatever
permission the git-trees endpoint needs, OR an owner-side
`git push` from any checkout lands it in one commit. That choice is
the parent's/owner's — this run was scoped to the retry + probe.

### Remaining after any successful push (unchanged)

- 41 binary PNG/ICO files (490,455 B) + profile `assets/cv.pdf` —
  browser-side step (connector is UTF-8-only, proven §6).
- Owner secrets: `ESO_RELEASE_PRIVATE_KEY` (eso-agent),
  `STATS_TOKEN` (seneso.github.io) — exact paths in §6.
- Trigger the `release` workflow once with version `1.2.0`.
- Pages source = GitHub Actions on both repos if not auto-enabled.

ESO (laptop) and the Simular box deployment were not touched. No
secret material appears in this section.


## 9. Laptop push attempt 2026-11-18 ~18:45 — staged on ESO, but NO authenticated GitHub transport exists there; pushes NOT performed

The owner ordered the pushes done from the laptop (ESO). This run
staged everything on ESO and probed auth firsthand. Outcome: **the
pushes could not be performed — ESO has no working GitHub credential
of either transport.** Nothing reached GitHub; remote state is
unchanged (eso-agent still at `a86a43a7`, empty README only;
seneso.github.io still empty). No credentials were created,
extracted, or printed anywhere in this run.

### Staging — DONE and verified

- Payloads (complete trees incl. binaries, from
  `~/workspace/launch-push/`): eso-agent 221 tracked files @
  `b6c4ba2` "EsoAgent v1.2.0"; seneso.github.io 9 tracked files
  (incl. `assets/cv.pdf`) @ `1e86ae3` "Personal profile site".
- Per-file `scp -r` over the tunnel was too slow (204 of 479 files
  in ~7 min) and was killed; both repos were tarred locally and the
  two archives (1.8 MB + 152 KB) copied in ~49 s, then extracted on
  ESO with Windows `tar.exe` into **`D:\ivmp-work\launch-push\`**
  (the SADA lane working concurrently on ESO was not disturbed).
- Verified on ESO (git 2.55.0.windows.3): `ls-files` counts 221 / 9,
  log lines match the payload commits, profile tree clean. The
  eso-agent working tree shows 5 "modified" `.sh` files
  (`install/*.sh`, `scripts/*.sh`) — a CRLF checkout artifact of
  Windows git only; the commits themselves are byte-identical and
  unaffected.
- Both staged repos now carry their `origin` remote
  (`https://github.com/SENESO/eso-agent.git` /
  `https://github.com/SENESO/seneso.github.io.git`), so the
  remaining pushes are one command each (below).

### Auth probe on ESO — read-only, no secrets printed

- **SSH**: `C:\Users\Elmis\.ssh` does not exist; `ssh -T
  git@github.com` (BatchMode, scratch known_hosts) →
  `Permission denied (publickey)`. Windows OpenSSH agent is not
  running. → SSH transport NOT authenticated.
- **HTTPS / Git Credential Manager**: git global
  `credential.helper=manager`, GCM exe present. Presence-only query
  (`git credential-manager get` for host github.com,
  `GCM_INTERACTIVE=never`): **no username, no password stored**.
  `cmdkey /list` shows no github/git targets at all. → HTTPS
  transport NOT authenticated.
- **gh**: installed; account `SENESO` configured (hosts.yml
  exists, content not read) — but `gh auth status` reports **"The
  token in default is invalid."** → gh channel dead.
- **Fail-fast push proof**: `git push --force origin main` from
  the staged eso-agent repo with `GIT_TERMINAL_PROMPT=0` +
  `GCM_INTERACTIVE=never` failed immediately:
  `fatal: could not read Username for 'https://github.com':
  terminal prompts disabled` (exit 128). No prompt was left
  hanging anywhere.

### The one owner action that unblocks everything (at the laptop, once)

Sign in to GitHub **once** in a git context on ESO — e.g. open a
terminal in `D:\ivmp-work\launch-push\eso-agent`, run
`git push --force origin main`, and complete the Git Credential
Manager browser sign-in as **SENESO**. That single sign-in stores
the credential, the force-push lands the clean v1.2.0 history over
the 3 junk commits, and the second repo then pushes with no
further sign-in:

```bat
cd /d D:\ivmp-work\launch-push\eso-agent
git push --force origin main
cd /d D:\ivmp-work\launch-push\seneso.github.io
git push -u origin main
```

(Alternative equally-good one-time sign-in: `gh auth login` fresh,
then the same two pushes.) After the pushes, connector-side tree
verification (221 / 9 files incl. the binary spot-checks and the
stats.json `treeLines: 1340188` check) still needs to run — it was
step 4 of this lane and is NOT done, since there was nothing new
to verify.

### Remaining owner items (unchanged, after the pushes)

- Secrets: `ESO_RELEASE_PRIVATE_KEY` (eso-agent), `STATS_TOKEN`
  (seneso.github.io) — exact paths in §6.
- Trigger the `release` workflow once with version `1.2.0`.
- Pages source = GitHub Actions on both repos if not auto-enabled.

The Simular box was not touched. No secret material appears in
this section.

## 10. Pushes EXECUTED from ESO 2026-11-18 ~19:04 — BOTH REPOS LIVE, connector-verified

The §9 blocker resolved in an unexpected way: the owner's device-flow
authorization DID store a valid gh token — but in a keyring bound to
his **interactive logon session**, invisible from the SSH session.

### Evidence chain

- From the SSH session, everything still looked dead after his
  sign-in: `gh auth status` → "The token in default is invalid",
  `gh api user` → 401, `hosts.yml` (rewritten 18:59:57, during his
  flow) holds the SENESO user entry but **no `oauth_token`**, and
  `cmdkey /list` shows no gh target. No gh process was running.
- The pushes were therefore run inside his interactive session via a
  one-time scheduled task (`LaunchPushOnce`, `schtasks /it`, created,
  run, log captured, task deleted). Inside that session:
  `gh auth status` → **"Logged in to github.com account SENESO
  (keyring)"**, token scopes `gist, read:org, repo, workflow`
  (log: `D:\ivmp-work\launch-push\push-log.txt` on ESO).
- Lesson for every future ESO git operation: gh/GCM credentials on
  this laptop are interactive-session-bound — run git pushes from
  the interactive session (scheduled task), never expect them to be
  visible over SSH.

### Push results (both exit 0)

- **eso-agent**: `git push --force origin main` →
  `+ a86a43a...b6c4ba2 main -> main (forced update)` — the clean
  single-commit v1.2.0 history replaced the 3 junk commits, as
  intended.
- **seneso.github.io**: `git push -u origin main` →
  `* [new branch] main -> main`, upstream set.

### Connector verification (this side, after the pushes)

- Remote heads are byte-identical to the staged commits:
  eso-agent `b6c4ba2ae77f49a458a581bc0e015aacdc8fc74b`
  ("EsoAgent v1.2.0"), seneso.github.io
  `1e86ae3101796236d642dc0fd7f196fb4d8b36ac`
  ("Personal profile site") — equal commit SHAs make the full trees
  (221 / 9 files) identical by git content addressing.
- Spot checks via `get_file_contents`, blob SHAs matching local
  exactly: `assets/brand/png/mark-256.png` (real PNG),
  `src/EsoAgent.Tray/Assets/tray-online.ico` (real ICO),
  `CHANGELOG.md`, profile `assets/cv.pdf` (real PDF).
- Profile `data/stats.json` on GitHub contains
  `"treeLines": 1340188` for ivmp-platform. ✓

### Remaining owner items (unchanged — NOT done by this lane)

- Secrets: `ESO_RELEASE_PRIVATE_KEY` (eso-agent), `STATS_TOKEN`
  (seneso.github.io).
- Trigger the `release` workflow once with version `1.2.0`.
- Pages source = GitHub Actions on both repos if not auto-enabled.

The Simular box was not touched. No secret material appears in
this section (the token itself was never read — only gh's own
masked status output).

## 11. Release CI fix 2026-11-18 ~19:55 — CS1579 in Tray, root-caused and landed

The first `release` workflow run failed on BOTH build jobs at the
Tray publish with:
`src/EsoAgent.Tray/DesktopSessionHandler.cs(415,37): error CS1579:
foreach statement cannot operate on variables of type 'void'`.

### Diagnosis — a floating LangVersion, not a code bug CI "caught"

- Line 415 was `foreach (var vk in vks.Reverse())` where `vks` is a
  `ushort[]`. Two extension methods named `Reverse` are applicable to
  an array: `System.Linq.Enumerable.Reverse` (returns
  `IEnumerable<ushort>`) and `System.MemoryExtensions.Reverse`
  (takes `Span<ushort>` — which an array converts to implicitly —
  and returns **void**). Which one wins is a language-version
  overload-resolution question.
- `Directory.Build.props` sets `<LangVersion>latest</LangVersion>`
  and the repo has **no global.json**. Locally, SDK 8.0.425 speaks
  C# 12, whose resolution picks `Enumerable.Reverse` — a CLEAN
  rebuild + the exact CI publish command
  (`dotnet publish src/EsoAgent.Tray -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none`)
  succeeds locally, genuinely (bin/obj wiped first). On the GitHub
  runners, `actions/setup-dotnet` with `8.0.x` installs alongside
  the SDKs the images preinstall, and with no global.json the
  `dotnet` host selects the NEWEST SDK — whose newer C# overload
  rules prefer the implicit Span conversion, binding
  `MemoryExtensions.Reverse` → `void` → CS1579. Same tree, two
  language versions, two different programs.
- Mechanism proven locally with a minimal repro: with `System.Linq`
  out of scope, the identical expression on a `ushort[]` produces
  the identical error pair (CS1501 + CS1579) under SDK 8 itself —
  `MemoryExtensions.Reverse` is the void binding. (A live SDK 9
  repro was attempted; the SDK payload download is blocked by this
  sandbox's egress proxy, so the runner-side half rests on the
  documented resolution-rule change + the CI failure itself.)
- The codebase's only other `.Reverse()` calls (App's
  ActivityView) sit on `.AsEnumerable()` receivers, which cannot
  convert to Span — unambiguous under every LangVersion, untouched.

### Fix (one line)

`vks.Reverse()` → `Enumerable.Reverse(vks)` — an explicit static
call; no extension resolution is involved, so it binds identically
under C# 12, 13, 14 and beyond.

### Verification (local, SDK 8.0.425)

- Full solution build: **0 warnings / 0 errors**.
- Exact CI Tray publish command, clean tree: succeeds,
  `EsoAgent.Tray.exe` produced.
- Test suite (xUnit console runner, merged NETCore+AspNetCore
  runtimeconfig per §12): **Total: 184, Errors: 0, Failed: 0** —
  the previous 184/184 holds.

### Landing

- Fixed file committed in `~/workspace/launch-push/eso-agent` as
  `05bb14d`, then synced to ESO's staged repo
  (`D:\ivmp-work\launch-push\eso-agent`) and committed + pushed
  there via the §10 interactive-session route (one-time
  `schtasks /it` task `FixPushOnce`, created → run → log captured →
  deleted; push log `fixpush-log.txt` on ESO, `PUSH_EXIT=0`).
- Connector-verified: remote `main` head is now
  `c2754df691231f3242723dee0bf2dc1e62575e28` ("Fix Tray desktop
  session build (CS1579)", Eslam Ashraf) and the file on GitHub
  contains `Enumerable.Reverse(vks)`. (The ESO commit SHA differs
  from the local launch-push one only by commit timestamp; the
  pushed head is the authoritative one.)
- The release workflow was NOT re-triggered by this lane — that
  stays the parent's browser step. Follow-up worth doing later
  (not release-blocking): pin the SDK with a global.json so CI
  language semantics stop floating.

## 12. Release CI fix 2026-11-18 ~20:21 — installer step verify-install self-copy, landed

Release run #2 (on head `c2754df`) split the difference:
**build-linux SUCCEEDED** — the §11 CS1579 fix held — but
**build-windows failed** in step "Build the installer (Inno
Setup)": `Copy-Item installer/windows/verify-install.ps1
installer/windows/` → "Cannot overwrite the item
D:\a\eso-agent\eso-agent\installer\windows\verify-install.ps1
with itself."

### Diagnosis — a copy line that should never have existed

- Inno Setup resolves `[Files]` `Source:` paths relative to the
  directory containing the `.iss` script. `EsoAgent.iss` sits in
  `installer/windows/` and its `[Files]` section reads
  `Source: "verify-install.ps1"; DestDir: "{app}"` — the script
  already lives beside the `.iss` in the repo, so it is picked up
  **in place** at installer-build time. The `.iss`'s only staging
  need is the other `[Files]` entry, `Source: "payload\*"`, which
  the step's first line (`Copy-Item -Recurse payload
  installer/windows/payload`) correctly provides. (Same
  relative-to-script resolution as the `.iss`'s
  `SetupIconFile=..\..\assets\brand\png\app-icon.ico`.)
- The failing line copied `verify-install.ps1` onto its own
  location — source and destination are the same file (proven
  locally: both paths realpath to one file). It was presumably
  written on the assumption the script had to be staged like the
  payload; the `.iss` evidence says otherwise. The `[Run]`
  section's reference is to `{app}\verify-install.ps1` — the
  installed copy at install time, not a build-time path.

### Fix (one line, workflow only)

Deleted `Copy-Item installer/windows/verify-install.ps1
installer/windows/` from the installer step in
`.github/workflows/release.yml`. Diff against the previous head
is exactly that one deletion; every other step is byte-identical.

### Verification (sandbox)

- YAML syntax pass: `yaml.safe_load` parses the fixed workflow;
  the installer step's run block is now the payload copy, the
  ISCC invocation, and `Get-ChildItem dist`.
- Filesystem mock of the step: with `payload/` staged beside the
  `.iss`, both `[Files]` sources (`payload\*`,
  `verify-install.ps1`) resolve relative to the `.iss` directory
  with no copy of the script.
- Honest limit: there is no PowerShell or Inno Setup in this
  sandbox, so the step itself could not be executed here — the
  CI re-run is the real proof.

### Landing

- Fixed file committed in `~/workspace/launch-push/eso-agent` as
  `5c27047`, synced to ESO's staged repo
  (`D:\ivmp-work\launch-push\eso-agent`), committed + pushed
  there via the §10 interactive-session route (one-time
  `schtasks /it` task `FixPushOnce2`, created → run → log
  captured → deleted; push log `fixpush2-log.txt` on ESO,
  `PUSH_EXIT=0`). The ESO commit is targeted (`git add` of the
  workflow file only) — the pre-existing unrelated `.sh`
  working-copy modifications in that staged repo were NOT swept
  in. (The ESO commit SHA differs from the local launch-push one
  only by commit timestamp, as in §11.)
- Connector-verified: remote `main` head is now
  `de800c93f993dd650767c0eea98ebfa9df3994d0` ("Fix release
  installer step (verify-install self-copy)", Eslam Ashraf), and
  the workflow file served from that commit no longer contains
  the self-copy line while the payload staging line remains.
- The release workflow was NOT re-triggered by this lane — that
  stays the parent's browser step.

## 13. Release CI fix 2026-11-18 ~20:35 — Inno script "Variable Expected" (line 109), landed

Release run #3 (on head `de800c93`): **build-linux SUCCEEDED**
and the installer step got past the copy stage — the §12 fix
held — but Inno Setup aborted compiling
`installer/windows/EsoAgent.iss`: "Error on line 109 in
EsoAgent.iss: Column 70: Variable Expected. Compile aborted."

### Diagnosis — `nil` passed to a `var` parameter (plus a phantom 5th argument)

Line 109 was:

```pascal
if not GetSpaceOnDisk(ExpandConstant('{autopf}'), True, FreeMB, nil, nil) then
```

Inno Setup's prototype is:

```pascal
function GetSpaceOnDisk(const Path: String; InMegabytes: Boolean;
  var Free, Total: Cardinal): Boolean;
```

Exactly four parameters, and the last two are `var`
(by-reference) parameters. Column 70 is the comma immediately
after the first `nil` (the `nil` itself sits at columns 67–69),
i.e. the compiler flags that argument. Two defects on one line:

1. **A literal where a variable is required.** Pascal Script
   binds a `var` parameter to an actual variable (an l-value);
   `nil` is not one, so binding it raises exactly "Variable
   Expected". The call was written as if the unwanted `Total`
   output could be discarded with a `nil` pointer, C-API style
   — Inno Pascal Script has no such idiom; a dummy variable is
   the only way to receive-and-ignore an output.
2. **A fifth argument that does not exist** in the prototype
   (the second `nil`) — surplus even if the first had been a
   variable.

Sibling scan: `nil` occurred **only** on line 109 in the whole
script. The other calls that take `var` parameters already pass
real variables (`RegQueryStringValue(..., Existing)`,
`LoadStringsFromFile(..., Lines)`). No siblings to fix.

### Fix (2 lines, `EsoAgent.iss` only)

- Declared `TotalMB: Cardinal;` in `InitializeSetup`'s `var`
  block.
- The call now reads
  `GetSpaceOnDisk(ExpandConstant('{autopf}'), True, FreeMB, TotalMB)`
  — four arguments, variables in both `var` positions.
  (The call sits at line 110 after the added declaration.)

### Verification (sandbox)

- There is **no ISCC/Inno Setup in this sandbox**, so the script
  could not be compiled here — stated plainly; the CI re-run is
  the real proof.
- Re-read of the fixed region confirms a syntactically valid
  Inno Pascal Script construct per the documented prototype
  above; `grep` confirms no `nil` remains anywhere in the
  script.
- Diff against the previous head is exactly +2/−1 in the one
  file; nothing else touched.

### Landing

- Fixed file committed in `~/workspace/launch-push/eso-agent`
  as `2b71731`, synced to ESO's staged repo
  (`D:\ivmp-work\launch-push\eso-agent`), committed + pushed
  there via the §10 interactive-session route (one-time
  `schtasks /it` task `FixPushOnce3`, created → run → log
  captured → deleted; push log `fixpush3-log.txt` on ESO,
  `COMMIT_EXIT=0`, `PUSH_EXIT=0`, `de800c9..e6a0946`).
- Connector-verified: remote `main` head is now
  `e6a0946fd9dfade1b48b2f4bb38e392e5f84a470` ("Fix Inno script
  compile error (line 109)", Eslam Ashraf), and the blob SHA
  the connector reports for `installer/windows/EsoAgent.iss`
  at that commit (`c4e643272cee1f58e5aaa38fa8a1269737af3fbc`)
  is identical to `git hash-object` of the fixed local file —
  the pushed file is byte-for-byte the verified fix. (The ESO
  commit SHA differs from the local launch-push one only by
  commit timestamp, as in §11/§12.)
- The release workflow was NOT re-triggered by this lane —
  that stays the parent's browser step.

## 14. Release v1.2.0 SHIPPED 2026-11-18 20:34 — LAUNCH COMPLETE

- Secrets: `ESO_RELEASE_PRIVATE_KEY` set by the owner (19:19); `STATS_TOKEN`
  set by the owner from his laptop (19:37, fine-grained token
  "profile-stats-2", Contents Read-only, all repos — the first
  "profile-stats" token's value was lost in a clipboard mixup and the
  token was deleted 20:19; the STATS_TOKEN secret was proven end-to-end
  the same evening: update-stats run #1 succeeded and committed
  "chore: refresh site statistics" (448febf)).
- Release runs: #1 failed CS1579 (fixed §11), #2 failed installer
  self-copy (fixed §12), #3 failed Inno line 109 (fixed §13),
  **#4 SUCCEEDED** (run 44370583485, 3m 36s, on head e6a0946):
  version ✓, build-linux ✓, build-windows ✓ (Inno installer step
  45 s ✓, SignPath step skipped — not yet configured), release ✓.
- Release: https://github.com/SENESO/eso-agent/releases/tag/v1.2.0
  "EsoAgent v1.2.0" (Latest) — assets: esoagent-app.zip,
  esoagent-client.zip, esoagent-linux.zip, esoagent-server.zip,
  esoagent-windows.zip, esoagent_1.2.0_amd64.deb,
  esoagent-server_1.2.0_amd64.deb, EsoAgent-Setup-1.2.0.exe,
  manifest.json (signed) + source archives.
- Project site download button (releases/latest) now lands on v1.2.0. ✓
- Remaining: SignPath Foundation application (owner decision
  2026-11-18: apply after the first release — now unblocked).

## 16. SignPath Foundation application SUBMITTED 2026-11-18 21:04 — DECLINED 2026-11-19

The owner reviewed the browser-filled form (values per
SIGNPATH-APPLICATION.md), completed the discovery-channel field and
the consent checkboxes himself, and submitted. SignPath confirmed:
"Form submitted — Thank you, we'll be in touch soon." The v1.2.0
release notes gained the required code-signing mention the same
evening (application stated truthfully as pending; build unsigned
until approval).

Decline: SignPath replied 2026-11-19 (Phillip Deng, oss-support@
signpath.org): "we're unable to approve your application at this
time." Reason is not work quality — the Foundation program is for
projects with established public trust/visibility, measured by
external signals (GitHub stars/forks/contributors, external articles,
independent discussions, institutional support, sustained activity);
EsoAgent does not have enough yet. SignPath encouraged reapplying
once visibility grows. Releases stay unsigned meanwhile; the
immediate alternative is a paid SignPath subscription (owner
decision/hands; no price verified). Next steps on a future approval
are in SIGNPATH-APPLICATION.md §5 (owner creates the SignPath org +
MFA, adds SIGNPATH_API_TOKEN / SIGNPATH_ORG_ID /
SIGNPATH_PROJECT_SLUG, attribution line added to the policy page,
per-release approvals).

## 15. Installer defects found by the owner's first real install — fixed, patch 1.2.1 (2026-11-18 evening)

The owner ran `EsoAgent-Setup-1.2.0.exe` on his laptop. Three
defects, all owner-verified, all fixed in one commit. The headline
error: finish-page launch died with "Unable to execute file:
C:\Program Files\EsoAgent\EsoAgent.App.exe — CreateProcess failed;
code 2."

### Defect 1 — the App was never in the installer payload

- The `.iss` installs `payload\*` into `{app}` and then launches
  `{app}\EsoAgent.App.exe` ([Run] postinstall) and uses it as
  `UninstallDisplayIcon`. But the workflow's build-windows job
  published the App to `dist/pkg-app-windows` — never into
  `payload`. The payload carried only the Windows service + Tray +
  install scripts, so `{app}` had no `EsoAgent.App.exe`.
- The Tray also expects it: `TrayContext.cs:293,295` looks for
  `EsoAgent.App.exe` beside the tray exe, then at
  `C:\Program Files\EsoAgent\EsoAgent.App.exe` — both miss on a
  1.2.0 install.
- **Fix (workflow layer):** one added line in
  `.github/workflows/release.yml` — the App is now ALSO published
  into `payload` with the identical flags; the standalone
  `dist/pkg-app-windows` publish is untouched.
- **Verified locally:** the workflow's exact win-x64 publish
  commands were run into a scratch payload (SDK 8.0.425). Resulting
  tree vs every `.iss`/`install.ps1` reference:

  | Referenced path in {app} | Referenced by | In fixed payload |
  |---|---|---|
  | `EsoAgent.App.exe` (131,532,992 B) | .iss [Run] launch, UninstallDisplayIcon, Tray | YES (was NO) |
  | `EsoAgent.Windows.exe` | install.ps1 service binary (hard-throws if absent) | YES |
  | `EsoAgent.Tray.exe` | install.ps1, .iss [Registry] Run, install.ps1 HKCU Run | YES |
  | `install.ps1` / `uninstall.ps1` / `update-apply.ps1` | .iss [Run] / [UninstallRun] / install.ps1 | YES |
  | `verify-install.ps1` | .iss [Files] + [Run] | YES (staged beside .iss) |
  | `esoagent-version.txt` | update machinery | YES |
  | `eso-client.exe` (client CLI) | nothing in the installed product references it in {app} | not added (ships as its own release zip) — checked, not a gap |

  Sidecar files in the merged payload (`web.config`,
  `eso-client.runtimeconfig.json`) collide with nothing.

### Defect 2 — no shortcuts anywhere ([Icons] findings)

- The shipped `.iss` had **no [Icons] section at all** (and
  `DisableProgramGroupPage=yes` with an unused `DefaultGroupName`),
  so Setup created no Desktop and no Start Menu entry — exactly
  what the owner reported.
- **Fix:** added `[Tasks]` (`desktopicon`, default on) and
  `[Icons]`: Start Menu `{group}\EsoAgent` → `{app}\EsoAgent.App.exe`
  (always), `{group}\Uninstall EsoAgent` → `{uninstallexe}`, and
  Desktop `{autodesktop}\EsoAgent` → `{app}\EsoAgent.App.exe`
  (task-gated). The Tray gets no shortcut by design (background
  companion, autostarts via [Registry]); the agent is a service.

### Defect 3 — automatic on-device setup never happened (parity audit)

Read-only probes on the owner's laptop after his 1.2.0 install
showed the true blast radius: **service NOT installed, no data
dir, no install.log, no firewall rule, port 8737 silent** — while
Inno-native steps ([Files], [Registry] HKLM tray entry,
uninstaller) had all worked. Both PowerShell [Run] steps
(install.ps1, verify-install.ps1) had produced zero effects.

Root cause, proven with the owner's own Windows PowerShell
5.1.26100.9818 parser (`Parser::ParseFile` over SSH):

- `install.ps1` — **7 parse errors**, first at line 98. The file
  is BOM-less UTF-8; PS 5.1 decodes it as ANSI, the em dash
  (E2 80 94) becomes a curly closing quote (0x94), which
  PowerShell accepts as a string delimiter — the string ends
  early and the rest cascades. Em dashes sat in 3 string literals.
- `verify-install.ps1` — **2 parse errors** at the
  `Log "agent port $Port: listening"` line: inside a double-quoted
  string, `$Port:` parses as a scope-qualified variable and is a
  hard parse error on any PowerShell. (This file had never once
  executed successfully anywhere.)
- Execution policy was checked and is NOT the cause (no GPO,
  a Bypass marker script runs fine). Parse failure = the scripts
  exited instantly, invisibly (runhidden), and Inno does not check
  [Run] exit codes — so setup "succeeded" with nothing configured.

Fixes:

- **All four shipped .ps1 are now pure ASCII** (em dashes purged
  from install.ps1 ×4, update-apply.ps1 ×2 — same parse-killer in
  its log strings, would have broken Windows auto-update
  identically — and verify-install.ps1 ×3), and verify's two
  `$Port:` are now `${Port}:`. `uninstall.ps1` was already clean.
  **Re-verified on the owner's machine: all four parse with
  0 errors under his PS 5.1 parser.**
- **Owner-password parity:** install.ps1's password step was an
  interactive `Read-Host` — unanswerable inside the hidden [Run]
  step, and it sat BEFORE service registration, so it would have
  blocked setup even with parsing fixed. install.ps1 gained an
  optional `-OwnerPassword` parameter (prompt kept for direct/zip
  runs); the `.iss` gained a wizard password page (min 8 chars,
  quote characters rejected, validated in `NextButtonClick`) whose
  value is passed single-quoted via `{code:GetOwnerPassword}`,
  with `/OWNERPASSWORD=` supported for silent installs.
- The three PowerShell [Run]/[UninstallRun] entries now use the
  full path `{sys}\WindowsPowerShell\v1.0\powershell.exe`.
- verify-install.ps1 additionally gates on `EsoAgent.App.exe`
  being present in the install dir, so a payload regression fails
  verification loudly instead of dying at the finish-page launch.

Parity table — scripted install (install.ps1 / v1.2 design) vs the
Inno installer after this fix:

| Automatic-setup item | Status |
|---|---|
| Windows service installed, auto-start at boot (`sc.exe create … start= auto`) | **Fixed** — was designed-in via [Run] install.ps1 but the script could not parse; now parses + runs |
| Agent actually running after install (`Start-Service` + verify gate) | **Fixed** — same root cause; verify-install.ps1 also unblocked |
| Tray runs at login | **Verified-present** — Inno [Registry] HKLM Run entry worked even in 1.2.0 (probe-confirmed); install.ps1's HKCU duplicate is redundant |
| Data dir + `esoagent.json` config + API token created | **Fixed** — install.ps1 creates them; was blocked by the parse failure |
| Firewall exception (agent port, tailnet-scoped `EsoAgent-In`) | **Fixed** — created by install.ps1 after the password step; was blocked twice over (parse + prompt order) |
| Owner password set during install | **Fixed** — wizard page → `-OwnerPassword`; was an unanswerable hidden prompt |
| First-run flow: device code on the finish page (first-run.txt) | **Fixed** — verify-install.ps1 writes it; was blocked by its own parse error |
| Fleet server port exception on a device install | **Not applicable** — the fleet server is a separate package, not part of the agent installer |
| Mesh enablement | **Not applicable** — opt-in in BOTH routes (install.ps1 default off); not an automatic-setup item |
| uninstall.ps1's data prompt under [UninstallRun] (hidden) | **Verified-acceptable** — service/firewall/autostart cleanup runs before the prompt; the .iss asks about data itself in `CurUninstallStepChanged`; a failed prompt degrades to the safe keep-data branch |

### Version — prepared as patch release 1.2.1

- `Directory.Build.props` (the single version source): 1.2.0 →
  1.2.1 (Version, AssemblyVersion, FileVersion,
  InformationalVersion).
- The `.iss` carries a manual mirror (`#define AppVersion`) that
  the workflow never stamps — bumped to 1.2.1 as well, or the
  1.2.1 release would have shipped a self-mislabelled installer.
  `AppId` deliberately untouched (upgrade identity; the owner's
  broken 1.2.0 install will be repaired by an in-place 1.2.1
  install over the same dir/service/data design).

### Landing

- Committed in `~/workspace/launch-push/eso-agent` as `26dbb52`
  (Eslam Ashraf, "Fix installer app launch path
  (EsoAgent.App.exe missing)"), 6 files, +107/−22; synced to
  ESO's staged repo and pushed via the §10 interactive-session
  route (one-time `schtasks /it` task `FixPushOnce4`, created →
  run → log `fixpush4-log.txt` captured → deleted;
  `COMMIT_EXIT=0`, `PUSH_EXIT=0`, `e6a0946..94c3ca5`).
- Connector-verified: remote `main` head is now
  `94c3ca5f988bfc33e71383f9f6d6b0d444ea10d4`, and the blob SHA the
  connector reports for `installer/windows/EsoAgent.iss` at that
  commit (`1d27e7933f69520457de0628813ea42db3caefe6`) is identical
  to `git hash-object` of the fixed local file;
  `Directory.Build.props` at that commit carries 1.2.1.
- The release workflow was NOT re-triggered by this lane — the
  parent triggers run 1.2.1. Honest limit: Inno/ISCC cannot run
  in this sandbox, so the .iss changes (password page, [Icons],
  [Tasks]) are verified by construction + the payload/parse
  proofs above; the CI release run + the owner's reinstall are
  the final compile/runtime proof.

## 17. Inno identifier error at line 178 — fixed AND compile-proven on ESO (2026-11-18 night)

The 1.2.1 release run failed on a NEW Inno compile error from the
§15 additions: `installer/windows/EsoAgent.iss` line 178, column 70:
"Identifier expected. Compile aborted."

### Diagnosis — a `{code:...}` literal inside a Pascal `{ }` comment

Line 178 sat inside the comment above the password-page creation in
`InitializeWizard`:

```
  { Owner password page: install.ps1 runs hidden, so its interactive
    prompt can never be answered inside the GUI installer - the
    password is collected here and passed via {code:GetOwnerPassword}. }
```

Pascal Script `{ }` comments end at the FIRST `}` — comments do not
nest and string-style escapes do not exist inside them. The `}` of
the embedded `{code:GetOwnerPassword}` (line 178, column 70 —
exactly the reported column) terminated the comment early, leaving
the trailing `. }` to be parsed as code: a member-access dot with no
identifier after it → "Identifier expected". The construct was
introduced by §15's password-page comment; the rest of the §15
additions ([Tasks], [Icons], the {code:} usage in [Run]) are sound.

### Fix (one comment reworded, +2/−1, .iss only)

The comment now reads "…passed to install.ps1 through the
GetOwnerPassword code function referenced by the [Run] entry." — no
braces anywhere inside it. No code, section entry, or behaviour
changed.

### Exhaustive scan of the whole .iss (third .iss failure — no fourth)

- A character-walk of the entire [Code] section (tracking
  string/comment state, honouring `''` escapes and `//` comments):
  ZERO nested `{` inside comments, ZERO stray `}`, no unterminated
  strings, balanced at EOF. The other five `{ }` comments in [Code]
  contain no braces in their bodies.
- `{code:}` occurs only in the [Run] Parameters (both functions
  declared with the required `(Param: string): string` signature);
  `{param:...|}` occurs only inside quoted strings passed to
  ExpandConstant; `{{` only in AppId; `{#...}` ISPP defines all
  defined. Constants used ({autopf}, {app}, {group}, {autodesktop},
  {uninstallexe}, {sys}) are all standard.
- [Code] Pascal reviewed line by line: CreateInputQueryPage /
  CreateOutputMsgPage usage, `Values[0]`, `Pos('''', ...)`,
  the §13-fixed 4-argument GetSpaceOnDisk, LoadStringsFromFile with
  a TArrayOfString var, DelTree — all valid Inno Pascal Script.
- Section grammars ([Tasks] continuation, [Icons], the [Registry]
  `"""{app}\..."""` quoting, [Run] embedded `""` quotes) — all
  standard, and now all compile-proven below, not just reviewed.

### REAL COMPILE PROOF on ESO (compile only — installer NOT run)

- Inno Setup **6.7.4** installed fresh from the official GitHub
  release asset (jrsoftware/issrc `is-6_7_4`, 10,604,576 B) to
  `D:\ivmp-work\tools\InnoSetup6` (silent install, exit 0). No Inno
  install existed on the machine beforehand. (The old
  jrsoftware.org `download.php/is.exe` link now 404s.)
- Payload assembled with the workflow's exact win-x64 publish
  commands (SDK 8.0.425): EsoAgent.Windows + EsoAgent.Tray +
  EsoAgent.App single-file publishes into `payload`, the three
  install scripts copied in, `esoagent-version.txt` = 1.2.1 —
  9 files, 399 MB; App exe 131,532,992 B, byte-size-identical to
  §15's verified payload. (Sandbox note: /tmp is a 512 MB tmpfs —
  the first attempt died with "No space left on device"; the build
  was redone under ~/workspace.)
- Proof tree on ESO at `D:\ivmp-work\iscc-proof\` laid out exactly
  as the workflow stages it (installer\windows\EsoAgent.iss +
  verify-install.ps1 + payload\, assets\brand\png\app-icon.ico for
  the relative SetupIconFile path); tar verified byte-identical
  after transfer (417,689,600 B).
- **ISCC result (compiler engine: Inno Setup 6.7.4):**
  "Successful compile (84.422 sec). Resulting Setup program
  filename is: D:\ivmp-work\iscc-proof\dist\EsoAgent-Setup-1.2.1.exe"
  — **ISCC_EXIT=0**, output 127,444,918 B. Full log:
  `D:\ivmp-work\iscc-proof-compile.log`. The produced installer was
  NOT executed — the owner's own reinstall is the final test.
  One benign warning (pre-existing pattern, not a §15/§17 item):
  the single [UninstallRun] entry has no RunOnceId.

### Landing

- Committed in `~/workspace/launch-push/eso-agent` as `e78d691`
  (Eslam Ashraf, "Fix Inno identifier error at password page
  (line 178)"), synced to ESO's staged repo and pushed via the §10
  interactive-session route (one-time `schtasks /it` task
  `FixPushOnce5`, created → run → log `fixpush5-log.txt` →
  deleted; `COMMIT_EXIT=0`, `PUSH_EXIT=0`, `94c3ca5..5a886c6`).
- Connector-verified: remote `main` head is now
  `5a886c6002015799e670ee5f8566bf93d9322a4f`, and the blob SHA the
  connector reports for `installer/windows/EsoAgent.iss` at that
  commit (`4f9e4ae42c9d32c39b761b2b4fc4313350435c5d`) is identical
  to `git hash-object` of the fixed local file — the pushed .iss is
  byte-for-byte the file ISCC compiled successfully on ESO.
- The release workflow was NOT re-triggered by this lane — the
  parent triggers run 1.2.1.

## 18. Release v1.2.1 SHIPPED 2026-11-18 21:33

Run #6 (44381174445, on head 5a886c6 whose .iss was compile-proven
on ESO with Inno Setup 6.7.4, §17): all jobs green. Release
https://github.com/SENESO/eso-agent/releases/tag/v1.2.1 (Latest) —
9 assets incl. EsoAgent-Setup-1.2.1.exe + signed manifest.json;
releases/latest resolves to v1.2.1. Remaining proof: the owner's
in-place reinstall on his laptop (repairs the broken 1.2.0 install,
same AppId) — shortcuts, automatic setup, first-run device code.


## 17. Releases v1.2.2 + v1.2.3 and the verified laptop install (2026-11-19/20)

v1.2.2 (Release run #7, commit dba5880, 2026-11-19 18:03): the .iss
NextButtonClick validated the wizard-page password value, which is
empty in silent mode, so /OWNERPASSWORD= installs died on a MsgBox.
Fix: validate the EFFECTIVE password (param wins, else page value).

v1.2.3 (Release run #8, commit a740bf7, 2026-11-19 19:07): install.ps1
died in 0.4 s under the Inno path — written for the zip layout, it
copied EsoAgent.Windows.exe onto itself when Inno ran it in place
inside {app}, and ErrorActionPreference=Stop killed it before service
registration. Fix: Copy-IfNewLocation guard on the 3 copy lines.
Latent since 1.2.1; nobody had run the Inno path end-to-end before.

Laptop install (owner order 2026-11-19, completed 2026-11-20 10:39):
official v1.2.3 installer byte-verified (124,427,842 B, SHA-256
f5c6666b…fbd matches the GitHub asset digest) after three corrupted/
truncated download attempts on 2026-11-19 (resume +303 KB over size;
clean copy cut at exactly 60 MiB; third cut by a runtime restart +
laptop sleep). Silent install via WMI with a generated 24-char owner
password: Inno EXIT=0. Verified: service EsoAgent Running/Automatic,
port 8737 listening, esoagent-version.txt = 1.2.3, data dir complete
(token, owner.json 129 B, cert.pfx, device.key, esoagent.json),
Public-Desktop + Start Menu shortcuts present. Standalone mode:
fleet.enabled=false; the device code is minted at fleet enrollment —
enrollment + the two-device proofs (mesh peer + relay fallback, DXGI
desktop, app-vs-agent) are the next step. Owner password stored at
~/.eso-secrets/laptop-agent-owner-pw (location only, never the value).

UI overhaul (owner verdict 2026-11-20: UI not good, missing things,
harder than competitors, not polished like TeamViewer): audit +
20-gap table + 6-phase redesign in UI-OVERHAUL.md.

## 19. Fleet server tailnet join — Tailscale on the box, stopped at the owner's approval gate (2026-11-20)

Owner decision 2026-11-20 (he delegated the architecture: "do the best,
most professional, strongest"): the first plan (host the fleet server on
the Muse VM) died on a verified fact — this VM is a tailnet client
only, accepts no inbound connections, hosts nothing
(~/docs/devices/tailscale.md). Chosen instead: keep the ONE fleet server
on the always-on box and join the BOX to the tailnet; the laptop then
enrolls over the tailnet. Fallback (server on the laptop) not triggered.

Done: Simular shell re-verified (it was already allowlisted since
2026-11-18; recorded in the connector doc); box woken from hibernation;
Tailscale 1.104.7 installed from the official source; tailscaled runs in
TUN mode with a proxy drop-in (the box has no direct egress — everything
goes through Simular's local proxy at 127.0.0.1:3128). Root-caused a
silent join hang: with an empty IPv4 routing table, tailscaled's netmon
declared "network down" and parked its control client in waitUnpause
(proven by goroutine dump; TUN and userspace alike). Fix: a synthetic
uplink (dummy0 10.99.0.1/24 + default route, persisted as boot unit
eso-dummy-net.service) — the auth URL was issued seconds later.
Fingerprint pinning verified compatible by source: the fleet client
pins the server's SHA-256 cert fingerprint with no hostname/SAN check,
so tailnet-IP connections keep pinning intact; no serve/TLS
termination needed.

STOPPED at the owner's gate: device "simular-box" awaits Eslam's
Tailscale approval (login link in FLEET-MOVE-REPORT.md; tailscaled +
the up process left running and waiting). After approval: restart
esoagent-server (binder adds the tailnet interface; verify :8740 on
tailnet IP + loopback only), then laptop enrollment + the two-device
proofs. Full record: FLEET-MOVE-REPORT.md.
