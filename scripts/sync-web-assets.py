#!/usr/bin/env python3
"""Sync the shared web assets into the four panel HTML files.

The panels are single-file embedded resources (served verbatim), so the one
design source in src/EsoAgent.Web/ is inlined between marker comments:

  Fleet.html / Dashboard.html:
    /* ESO:TOKENS */ ... /* /ESO:TOKENS */         <- tokens.css
    /* ESO:COMPONENTS */ ... /* /ESO:COMPONENTS */ <- components.css
    // ESO:COMPONENTS-JS ... // /ESO:COMPONENTS-JS <- components.js
  FleetLogin.html / DashboardLogin.html:
    /* ESO:TOKENS */ ... /* /ESO:TOKENS */         <- tokens.css
    /* ESO:LOGIN */ ... /* /ESO:LOGIN */           <- login.css

Run after editing anything in src/EsoAgent.Web/:
  python3 scripts/sync-web-assets.py
Exits non-zero if a marker is missing (file not yet migrated).
"""
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
WEB = ROOT / "src" / "EsoAgent.Web"

def block(text, start, end, content, name):
    pat = re.compile(re.escape(start) + r".*?" + re.escape(end), re.S)
    if not pat.search(text):
        raise SystemExit(f"marker {start!r} not found in {name}")
    return pat.sub(lambda _: start + "\n" + content.rstrip() + "\n  " + end, text, count=1)

def sync(path, css_blocks, js_blocks):
    text = path.read_text()
    for start, end, src in css_blocks:
        text = block(text, start, end, (WEB / src).read_text(), path.name)
    for start, end, src in js_blocks:
        text = block(text, start, end, (WEB / src).read_text(), path.name)
    path.write_text(text)
    print("synced", path.relative_to(ROOT))

PANEL_CSS = [("/* ESO:TOKENS */", "/* /ESO:TOKENS */", "tokens.css"),
             ("/* ESO:COMPONENTS */", "/* /ESO:COMPONENTS */", "components.css")]
PANEL_JS = [("// ESO:COMPONENTS-JS", "// /ESO:COMPONENTS-JS", "components.js")]
LOGIN_CSS = [("/* ESO:TOKENS */", "/* /ESO:TOKENS */", "tokens.css"),
             ("/* ESO:LOGIN */", "/* /ESO:LOGIN */", "login.css")]

sync(ROOT / "src/EsoAgent.Server/Fleet.html", PANEL_CSS, PANEL_JS)
sync(ROOT / "src/EsoAgent.Core/Dashboard.html", PANEL_CSS, PANEL_JS)
sync(ROOT / "src/EsoAgent.Server/FleetLogin.html", LOGIN_CSS, [])
sync(ROOT / "src/EsoAgent.Core/DashboardLogin.html", LOGIN_CSS, [])
print("done")
