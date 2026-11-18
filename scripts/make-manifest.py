#!/usr/bin/env python3
"""Builds dist/manifest.json for the release channel (item 45) and signs
it with the release key when present (.release-key/release-private.pem
locally, ESO_RELEASE_PRIVATE_KEY in CI). The signature is ECDSA P-256 /
SHA-256 in P1363 form over ReleaseManifestSigning.Canonical — devices
verify it against the pinned public key before trusting any artifact."""
import base64, hashlib, json, os, subprocess, sys

DIST, VERSION = sys.argv[1], sys.argv[2]
REPO = os.environ.get("ESO_RELEASE_REPO", "SENESO/eso-agent")
BASE = f"https://github.com/{REPO}/releases/download/v{VERSION}"

def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

FILES = {
    "windows-agent": "esoagent-windows.zip",
    "linux-agent": "esoagent-linux.zip",
    "server": "esoagent-server.zip",
    "client": "esoagent-client.zip",
    "windows-app": "esoagent-app.zip",
    "linux-app": "esoagent-app.zip",
}
artifacts = {}
for key, name in FILES.items():
    path = os.path.join(DIST, name)
    if os.path.exists(path):
        artifacts[key] = {"url": f"{BASE}/{name}", "sha256": sha(path)}

manifest = {"version": VERSION, "artifacts": artifacts, "signature": ""}
canonical = "eso-release\n" + VERSION + "".join(
    f"\n{k}={artifacts[k]['sha256']}" for k in sorted(artifacts))

key_path = os.environ.get("ESO_RELEASE_KEY_PATH", "")
default_key = os.path.join(os.path.dirname(DIST), ".release-key", "release-private.pem")
if not key_path and os.path.exists(default_key):
    key_path = default_key
if key_path and os.path.exists(key_path):
    sig_der = subprocess.run(["openssl", "dgst", "-sha256", "-sign", key_path],
                             input=canonical.encode(), capture_output=True, check=True).stdout
    # DER -> P1363
    i = 2
    if sig_der[1] & 0x80: i += sig_der[1] & 0x7F
    i += 1
    lr = sig_der[i]; i += 1
    r = sig_der[i:i+lr]; i += lr
    i += 1
    ls = sig_der[i]; i += 1
    s = sig_der[i:i+ls]
    p1363 = r.rjust(32, b"\x00")[-32:] + s.rjust(32, b"\x00")[-32:]
    manifest["signature"] = base64.b64encode(p1363).decode()
    print(f"manifest signed with {key_path}")
else:
    print("WARNING: no release key found — manifest written UNSIGNED (CI signs it)")

import datetime
manifest["publishedAt"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
with open(os.path.join(DIST, "manifest.json"), "w") as f:
    json.dump(manifest, f, indent=2)
print(f"manifest.json: v{VERSION}, {len(artifacts)} artifacts")
