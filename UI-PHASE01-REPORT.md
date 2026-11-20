# EsoAgent UI Overhaul — Phase 0 + Phase 1 Report

**Date:** 2026-11-20 · **Scope:** Phase 0 (tokens & components) and Phase 1
(web panels) from `UI-OVERHAUL.md`. **Status:** implemented and measured;
NOT deployed, NOT committed — parent reviews and ships.
**Constraint compliance:** views only. No C# file was touched; no API route,
payload, or protocol changed. Endpoint parity verified mechanically (below).

---

## 1. Phase 0 — tokens & components (new files)

| File | What it is |
|---|---|
| `src/EsoAgent.Web/tokens.css` | The single design source. Light theme on `:root` (spec §3.2 values), refined-dark variant on `[data-theme="dark"]`, type scale 12/13/14/16/20/24, 4-pt spacing, radii 8/12/999, one dialog shadow, 36 px panel row height. |
| `src/EsoAgent.Web/components.css` | Shared component set built only on token variables: header/nav, buttons (act/primary/danger), inputs, tables (+ sortable headers, `.tablewrap`), badges (colour + dot + label), cards + stat cards + meters, banners, empty states, `.outbox`, `.logview`, chat message list, modal dialog, toasts, focus ring, 120–150 ms transitions. |
| `src/EsoAgent.Web/components.js` | `esoDialog` (promise-based modal: text/select/textarea/checkbox-group fields, validation, Esc/backdrop dismiss), `esoConfirm`, `esoTextDialog`, `esoToast`, `fmtBytes`, `fmtTime` (relative + absolute on hover), `kvTable` (flattens JSON to a key/value table). |
| `src/EsoAgent.Web/login.css` | Login card on the shared tokens; styles the bare `label/input/button` elements the server injects (`Panel.cs`/`Dashboard.cs` form fragments) plus `.error`, `.langrow`, brand mark. |
| `src/EsoAgent.App/Themes/EsoTokens.axaml` | Avalonia `ResourceDictionary` mirroring the token source: `ThemeDictionaries` Dark (brand default) + Light, colours/brushes + spacing/radii/`EsoRowHeight` 44. Tokens only — no App screen consumes them yet (that is Phase 2, per plan). Compiles as part of the App project (build green). |
| `scripts/sync-web-assets.py` | The "compile" step: panels are single-file embedded resources served verbatim, so the shared sources are inlined between `ESO:TOKENS / ESO:COMPONENTS / ESO:COMPONENTS-JS / ESO:LOGIN` markers in the four HTML files. Verified idempotent (md5-stable across re-runs). Edit `src/EsoAgent.Web/*`, re-run the script — never edit the inlined copies. |
| `scripts/check-contrast.py` | Scripted WCAG audit of every text/background pair the components use, both themes. |

**Measured deviation from spec §3.2 (one):** `--mut` is `#646B76`, not
`#6E7480` — the spec value sits at ~4.5:1 on the paper background with no
margin; `#646B76` measures 4.94:1 on `--bg`, 5.38:1 on `--surface`.
Semantic fills keep the spec values (`--ok #2F9E44`, `--warn #E08A00`,
`--bad #D64545` — none of them passes 4.5:1 as text: 3.35 / 2.9 / 4.0), so
each gained a text-safe `-tx` partner used for all text:
`--ok-tx #1E7A34`, `--warn-tx #8F5E00`, `--bad-tx #B02A2A` (light);
brightened equivalents in the dark variant.

**Contrast audit (scripted, `scripts/check-contrast.py`): 42/42 pairs
≥ 4.5:1, both themes.** Tightest pairs: `--ok-tx` on `--ok-bg` 4.80:1,
`--acc #8A6508` on `--bg` 4.89:1, `--warn-tx` on `--warn-bg` 4.93:1,
`--mut` on `--bg` 4.94:1. Primary text is 14.25–15.73:1 everywhere;
`--on-acc` on `--acc-solid` is 8.94:1.

---

## 2. Phase 1 — what changed per surface

### Fleet panel (`src/EsoAgent.Server/Fleet.html`)
- **Theme:** GitHub-dark clone deleted; soft-light tokens + shared
  components throughout. Gold appears only as accent (primary buttons,
  active nav underline, badge/link colour) and the wing mark.
- **Overview home added** (new default tab): stat cards — devices total
  (approved/rejected split), online (+ offline count), pending approvals,
  health failures, alerts/24 h — plus a "Waiting for approval" table with
  1-click Approve/Reject and a Recent activity table (8 latest). Data comes
  from the existing `/panel/api/devices|health|access|activity` endpoints.
- **"Device console" tab deleted** (G13 duplication): nav button, section,
  and all its JS are gone (0 references). Its job lives on the device page.
- **Devices table:** search box (code/name/OS/version/groups/IPs), state
  filter (all/online/offline/Pending/Approved/Rejected), group filter
  (populated from live data), sortable Code/Name/OS/State/Last-seen
  headers with direction arrows, empty state. Row actions unchanged.
- **All native dialogs replaced (16 call sites):** Rename → text dialog
  prefilled with the current name; Edit groups → text dialog prefilled,
  comma-parsed as before; Reject → danger confirm dialog; Wake result →
  non-blocking toast (no dismissal click); access-code create error →
  toast; grant/code Revoke, Revoke operator, Rotate operator key →
  confirm dialogs; enrollment-key Revoke → confirm dialog; script Delete
  → confirm dialog; script Run with no targets → toast; **Mesh subnet
  approval → dialog with the advertised subnets as checkboxes** plus an
  "additional CIDRs" field (extras already approved stay carried over).
- **Activity (fleet + device page) → real tables:** relative time
  (absolute on hover), source badge, device, kind + summary, result badge.
  Filters and JSON/CSV export links unchanged.
- **Fleet audit → table:** timestamp column split out when a line starts
  with an ISO timestamp, entry column otherwise; empty state added.
- **Device page Status:** quick-action results render as a key/value table
  for JSON bodies (`kvTable`), an output card for stdout bodies, a banner
  for errors — no more `<pre>` JSON dumps.
- **Device page Desktop:** opening the Desktop sub-tab (or the new primary
  Desktop CTA in the device header) **starts the session immediately** —
  the separate Start click is gone from the top flow (the button remains
  for restarting after a stop). Chat is a message list (bubbles), not a
  `<pre>` log.
- **Device page Files:** sizes humanised (`fmtBytes`), download/upload
  progress in human units, Rename prefilled with the current name in a
  dialog, Delete via confirm dialog.
- **Scripts:** run results render as one card per target device (status
  header + output card) instead of one `<pre>` block; script Updated
  column shows relative time.
- **Access codes/grants lists → tables** with state badges and Revoke
  buttons.
- **i18n:** existing 17 EN/AR keys kept; added `overview` and
  `searchDevices` (incl. placeholder translation via `data-i18n-ph`).
  Coverage is ≥ the pre-change level (dynamic bodies were and remain EN;
  full coverage is Phase 6 by plan).

### Agent dashboard (`src/EsoAgent.Core/Dashboard.html`)
- Same light tokens + shared components (one design with the fleet panel).
- **All 4 `confirm()` sites replaced:** Kill process (names the process),
  Uninstall software, Delete recording, Rotate API token — all danger
  confirm dialogs.
- **Log tab → `.logview` rows** (one row per line, hover surface) instead
  of a `<pre>` dump; empty-filter state handled.
- **Run / Software outputs → output cards** (`.outbox` divs).
- **Overview cards gain meters:** CPU, RAM, and per-disk used-% bars under
  the existing big numbers (data already returned by `/panel/api/status`).
- **Recordings:** size → `fmtBytes`, started → relative time.
- Processes table gains tabular numerals; Files download link restyled to
  the shared `a.act` button (same href/endpoint).
- Chat is a message list, same as the fleet panel.

### Login pages (`FleetLogin.html`, `DashboardLogin.html`)
- Rebuilt on tokens + `login.css`: warm paper background, white card, gold
  primary button, error banner on the bad tint with text-safe red.
- The dead `:focus-visible` rule that referenced an undefined `--acc` is
  fixed by construction — `--acc` is now defined by the token block.
- Server-injected form fragments (bare elements + `.error` div) verified
  against the new sheet; EN/AR toggle behaviour unchanged.

---

## 3. Measurements — before / after

| Metric | Baseline (audit) | After Phase 0+1 | Gate |
|---|---|---|---|
| `prompt(` / `confirm(` / `alert(` call sites, both panels | 20 (Fleet 16, Dashboard 4) | **0** (grep over all 4 HTML files) | = 0 ✓ |
| `#0d1117` anywhere in the HTML surfaces | present in all 4 files | **0** (also 0 in built DLLs) | = 0 ✓ |
| `<pre>` elements, Fleet + Dashboard | 10 | **0** | dumps → tables/cards ✓ |
| F3 — panel: open a device's live desktop | 3 clicks (Open → Desktop tab → Start) | **2 clicks** (Open → Desktop tab; session auto-starts) | 2 ✓ |
| F7 — panel: approve a pending device | 1 click | **1 click** (Devices table and Overview pending list) | stays 1 ✓ |
| F9 — panel: wake a device | 1 click + dismiss `alert()` | **1 click** + toast (no dismissal) | improved ✓ |
| Contrast, text pairs (scripted WCAG) | not measured (dark theme) | **42/42 ≥ 4.5:1**, min 4.80:1 | ≥ 4.5:1 ✓ |
| Endpoints called by panel JS with no matching C# route | — | **0 of 58** (cross-checked against `Panel.cs`/`PanelV12.cs`/`Dashboard.cs` route registrations) | parity ✓ |
| JS syntax (`node --check` on every inline script) | — | 4/4 blocks parse | ✓ |
| `$('id')` references with no matching element id | — | 0 (Fleet 121 refs, Dashboard 73 refs) | ✓ |
| `dotnet build EsoAgent.sln` | green, 0 warnings | **green, 0 warnings, 0 errors** | ✓ |
| Test suite (xunit console runner*) | 184 total, 180 pass, **4 fail** | 184 total, 180 pass, **4 fail — identical set** | no regression ✓ |

\* `dotnet test`/vstest cannot run in this sandbox (testhost socket is
proxied and times out — the standing AGENTS.md limitation), so the suite
ran via `xunit.console.dll` per the documented workaround. The 4 failures
are pre-existing sandbox-environment failures, identical before and after:
`RelayDesktopTests.RelayOpen_HelloIsSeededAsFirstChannelMessage`,
`RelayDesktopTests.RelayOpen_RunnerConsumesSeededHelloAndOpensSession`
(sandbox socket behaviour), `PackageUploadLimitTests.
SharedLimit_ExceedsKestrelDefaultAndRealPackages` (Kestrel assembly not
loadable in this runner layout), `EfficiencyTests.
PackageVersion_StampRoundTrip`. The project record of 184/184 is from the
owner's Windows environment; nothing in this change touches the code those
tests exercise (HTML resources only), and the failing set is byte-identical
to the pre-change baseline.

Embedded-resource proof: `strings` on the rebuilt assemblies —
`eso-server.dll`: `#F7F5F1` ×2, `#0d1117` ×0, `esoDialog` ×4, Overview
section present; `EsoAgent.Core.dll`: `#F7F5F1` ×2, `#0d1117` ×0,
`esoDialog` ×3.

---

## 4. Deferred (by the phase plan, not by omission)

- **Mesh ACL raw-text editing** remains a textarea (rule-table editor is
  in the redesign's remove/simplify list but not in Phase 1's exit
  criteria; the subnet `prompt()` — the Phase 1 item — is now a dialog).
- **Script targets** are still typed device codes (device multi-picker is
  not a Phase 1 exit criterion).
- **Panel desktop canvas** keeps its fixed 960×540 initial backing store
  (responsive backing store / scaling controls are G19, Phase 3).
- **Updates/rollout, Tasks, Tunnels UIs** — Phase 4, untouched here.
- **App screens** — Phase 2+; Phase 0 delivered only its token dictionary.
- **Full AR coverage of dynamic content** — Phase 6; static coverage kept
  at ≥ current level and extended for the new Overview/search strings.

## 5. Files changed

New: `src/EsoAgent.Web/{tokens.css, components.css, components.js,
login.css}`, `src/EsoAgent.App/Themes/EsoTokens.axaml`,
`scripts/sync-web-assets.py`, `scripts/check-contrast.py`.
Rebuilt: `src/EsoAgent.Server/Fleet.html`,
`src/EsoAgent.Server/FleetLogin.html`, `src/EsoAgent.Core/Dashboard.html`,
`src/EsoAgent.Core/DashboardLogin.html` (shared blocks inside them are
generated — re-run `scripts/sync-web-assets.py` after editing
`src/EsoAgent.Web/`).
Untouched: every `.cs` file, all routes, all payloads, the App's screens,
the tray, the site.
