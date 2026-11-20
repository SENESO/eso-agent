#!/usr/bin/env python3
"""WCAG contrast audit for the EsoAgent web token set.

Reads src/EsoAgent.Web/tokens.css, resolves every text/background pair the
components actually use, and fails (exit 1) if any pair is below 4.5:1.
Run: python3 scripts/check-contrast.py
"""
import re, sys, pathlib

CSS = pathlib.Path(__file__).resolve().parent.parent / "src" / "EsoAgent.Web" / "tokens.css"

def parse_vars(block):
    out = {}
    for m in re.finditer(r"--([\w-]+)\s*:\s*(#[0-9A-Fa-f]{6})\s*;", block):
        out[m.group(1)] = m.group(2)
    return out

def lum(hexc):
    def f(v):
        v = v / 255.0
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (int(hexc[i:i+2], 16) for i in (1, 3, 5))
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)

def ratio(a, b):
    la, lb = lum(a), lum(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)

text = CSS.read_text()
light_block = re.search(r":root\s*\{(.*?)\n\}", text, re.S).group(1)
dark_block = re.search(r'\[data-theme="dark"\]\s*\{(.*?)\n\}', text, re.S).group(1)
themes = {"light": parse_vars(light_block), "dark": parse_vars(dark_block)}

# (foreground token, background token) pairs used as text on a surface.
PAIRS = [
    ("txt", "bg"), ("txt", "surface"), ("txt", "surface-2"),
    ("mut", "bg"), ("mut", "surface"), ("mut", "surface-2"),
    ("acc", "bg"), ("acc", "surface"), ("acc", "surface-2"),
    ("on-acc", "acc-solid"),
    ("ok-tx", "surface"), ("ok-tx", "ok-bg"), ("ok-tx", "bg"),
    ("warn-tx", "surface"), ("warn-tx", "warn-bg"), ("warn-tx", "bg"),
    ("bad-tx", "surface"), ("bad-tx", "bad-bg"), ("bad-tx", "bg"),
    ("bad-tx", "bad-bg-strong"),
    ("txt", "acc-bg"),
]

fail = 0
for name, v in themes.items():
    print(f"--- {name} theme ---")
    for fg, bg in PAIRS:
        if fg not in v or bg not in v:
            print(f"  MISSING token: --{fg} or --{bg}")
            fail += 1
            continue
        r = ratio(v[fg], v[bg])
        mark = "OK " if r >= 4.5 else "FAIL"
        if r < 4.5:
            fail += 1
        print(f"  {mark} --{fg} {v[fg]} on --{bg} {v[bg]}: {r:.2f}:1")
print("RESULT:", "PASS" if fail == 0 else f"FAIL ({fail} pairs below 4.5:1)")
sys.exit(1 if fail else 0)
