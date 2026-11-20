# EsoAgent brand assets

**The golden wing** — angular gold wing + signal waves on matte black.
Owner's pick, 2026-11-18 (chosen over the mesh-eye and hex-E concepts;
the AI concept raster lives in `brand/` at the repo root for reference
only — it is NEVER shipped as a product asset).

## Layout

- `svg/` — vector masters. `mark.svg` (wing on transparent),
  `mark-tile.svg` (wing in the matte-black rounded tile),
  `logo-lockup-{dark,light}.svg` (mark + "EsoAgent" wordmark).
- `png/` — renders: `mark-{16,24,32,48,64,128,256,512}.png`,
  `app-icon-{sizes}.png` + `app-icon.png`, `logo-lockup-{dark,light}.png`,
  tray set `tray-{online,offline,controlled,locked}.ico` (+ `-48.png`
  previews) and `tray-controlled-pulse.ico`.
- `app.ico` — THE application icon (tile form, 16/20/24/32/48/64/128/256,
  PNG entries): embedded into every exe via `<ApplicationIcon>`, copied
  to `src/EsoAgent.App/Assets/` for the Avalonia window icon, and shipped
  by the Windows installer as `{app}\app.ico` for the shortcuts.

## Tray state treatments (same geometry, colour only)

- **online** — gold gradient.
- **offline** — desaturated grey.
- **being controlled** — gold with the waves lit (highlight); the Windows
  tray alternates with the pulse frame (~1 s) while a session is live.
- **locked** (kill switch) — slate.

## Regenerating

`python3 scripts/render-brand.py` — the geometry lives ONCE in that
script (100×100 viewBox coordinates); it emits the SVG masters from the
same coordinates it rasterizes, so vector and raster never drift. It
also copies the tray ICOs into `src/EsoAgent.Tray/Assets/` and the app
tray PNGs into `src/EsoAgent.App/Assets/`.

## Palette

- Gold gradient: `#F7DC8F → #E9BE57 → #C9962E → #9A7418`
- Product accent (UI): `#E3B341` on near-black (`#0D1117` family)
- Matte black tile: `#1C1C1C → #111111`
