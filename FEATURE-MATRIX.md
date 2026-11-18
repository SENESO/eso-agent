# EsoAgent — Competitive Coverage Matrix

Built from the 44-system remote-access census (TeamViewer, AnyDesk,
RustDesk, Tailscale, OpenSSH, Teleport, MeshCentral, RMMs, tunnelling
tools…). User counts are not the competition axis — features, systems,
and techniques are. The target is the union of the field, plus what is
beyond it.

**How to read:** every row is one census capability with EsoAgent's
honest state — **Have it** (version noted) · **In v1.2** (this release) ·
**Roadmap** (phase noted in ROADMAP.md) · **Refused** (one-line reason).
No row claims "Have it" for anything not built and tested; this is an
audit document, not marketing.

## ★ Rare features first (the differentiators)

| Rare feature (holder) | EsoAgent |
|---|---|
| Zero-trust signed execution — actions signed by a key the vendor never holds (GoTo Resolve ★) | **Have it (v1.0)** — operator commands AND stream opens are ECDSA-signed; the fleet server cannot forge them |
| Background/backstage tools without disturbing the user (ScreenConnect Backstage ★) | **Have it (v1.0)** — shell, services, files, processes run with no desktop session at all |
| Admin toolkit without a session (Dameware ★) | **Have it (v1.0)** — the whole API is session-less; desktop is one capability among many |
| Per-application / per-capability sharing (DWService ★, RustDesk toggles) | **In v1.2** — capability toggles (item 25) + scoped access-code grants (item 41) |
| Expiring session codes (RealVNC Code Connect ★) | **In v1.2** — access codes: owner-set expiry (minutes–days), use counts, instant revoke (item 41) |
| Attended consent gate (Teleport moderated sessions are the strict form) | **In v1.2** — per-device consent policy with timeout→deny (item 20); full moderated sessions: **Roadmap** |
| Browser-rendered remote desktop (Cloudflare / Guacamole ★) | **In v1.2** — relayed desktop inside the fleet panel (item 1/2) |
| Clientless share-a-live-session link (Guacamole ★) | **Roadmap** |
| Tunnelling native RDP/SSH through the product's own channel (ISL ★) | **In v1.2** — TCP/UDP tunnels carry any protocol, incl. RDP/SSH (items 5/24) |
| Self-host relay economics — everything on one small server (RustDesk ★) | **Have it (v1.0)** — fleet server + agents are the whole deployment; no SaaS |
| Fully self-hostable control plane (NetBird/Headscale ★) | **Have it (v1.0)** — there is no hosted plane; you run all of it |
| Credential vault + injection (BeyondTrust ★) | **Roadmap** |
| SSH certificate authorities (OpenSSH/Teleport ★) | **Roadmap** — our analog (signed execution + device ECDSA identity) is **Have it (v1.0)** |
| Tailnet-Lock-style signed node keys (Tailscale ★) | **Roadmap** (hardening) — enrollment approval exists (v1.0); signed record distribution is queued |
| Session handover + multi-expert invite; AR camera assist (TeamViewer ★) | **Roadmap** |
| Parsec-class latency stack (GPU encode, 4:4:4, virtual displays) | **Roadmap** (H.264 hardware encoding is phase 3) |
| Intel AMT / BMC out-of-band plane (MeshCentral ★) | **Roadmap** — integration story only; Wake-on-LAN is **In v1.2** |
| Layer-2 bridging (ZeroTier ★) | **Roadmap** |
| frp STCP secret services / XTCP P2P; ngrok edge policy + request replay | **Roadmap** |
| Mosh/ET transport roaming; Zellij resurrection | **Roadmap** — server-side persistence is covered: durable jobs **Have it (v1.1)**, persistent shells **In v1.2** |

## Beyond the field — in no competitor (our inventions)

| Technique | Status |
|---|---|
| **Perception stack** — UI structure trees → app data APIs → event streams → semantic renders → delta pixels → screenshots last, as the operator's default way of seeing | **In v1.2** (items 27/28/36/39) |
| **Delta perception** — structural diffs instead of repeated full dumps (measured: inventory 5,884 B → 132 B no-change) | **In v1.2** (item 37) |
| **Intent-level operations** — one declarative call, executed + verified on-device, returning a compact proof object | **In v1.2** (item 37) |
| **Cost-metered perception** — every see/perceive response reports its own bytes (≈ tokens); human-glance checklist as the acceptance test | **In v1.2** (item 39) |
| **Design-as-data** — Blender/Unreal/Godot scenes as diffable JSON + exact object-ID-pass measurement (bbox/coverage from the ID map, no estimation) | **In v1.2** (item 36) |
| **Quality-floored adaptive streaming** — adapts to bandwidth but never silently below the stated legibility floor (quality 40 / 4 fps); reports instead | **In v1.2** (item 37) |
| **One product, three faces, one state** — app, browser panel, and UI-less operator API/CLI over a single backend, registry, and activity timeline | **Have it (v1.0–v1.1)**, completed **In v1.2** (items 1–4) |
| **Anomaly-only awareness** — watches emit only crossings/events into one timeline; zero polling burn | **In v1.2** (items 29/31/34/37) |

## A. Session — desktop

| Capability | EsoAgent |
|---|---|
| Unattended agent | **Have it (v1.0)** |
| Attended session by code | **In v1.2** (access codes, item 41); by link: **Roadmap** |
| Multi-monitor | Capture all monitors **In v1.2** (vision pack); streaming is the primary monitor (v1.0); per-monitor stream windows: **Roadmap** |
| Virtual displays | **Roadmap** |
| Resolution auto-match / dynamic resize | **Roadmap** (stream is native resolution; quality/fps adapt) |
| Clipboard text sync + direction/capability control | **Have it (v1.0)**; per-capability toggle **In v1.2** (item 25) |
| File transfer: resumable, browser, manager UI | **Have it (v1.0)** chunked + SHA-256 verified; App file manager **In v1.2** (item 18); dual-pane/drag-drop: **Roadmap** |
| Remote printing | **Roadmap** |
| Audio / mic / hi-fi | **Roadmap** |
| USB / smartcard / camera / gamepad / pen redirection | **Roadmap** |
| Codecs: adaptive JPEG + raw-diff today | **Have it (v1.0)**; H.264/H.265/AV1: **Roadmap** |
| Privacy / black screen + input block | **In v1.2** (item 12; Linux input lock is partial — see V12-REPORT) |
| Wake-on-LAN | **In v1.2** (item 9) |
| Remote reboot / reconnect after reboot | **Have it (v1.0)**; reboot-to-safe-mode: **Roadmap** |
| Session recording | **In v1.2** (agent-side `.esorec`, item 6); searchable video audit: **Roadmap** |
| In-session chat | **In v1.2** (text, item 19); voice/video: **Roadmap** |
| Annotation / whiteboard / laser pointer | **Roadmap** |
| AR camera support | **Roadmap** |
| Multi-technician join / handover | **Roadmap** |
| Background tools (no session) | **Have it (v1.0)** |
| Remote exec without a session | **Have it (v1.0)** |
| Mobile device control | **Roadmap** |
| Browser client / native app / mobile technician app | Browser **Have it (v1.0)**, unified **In v1.2**; desktop app **Have it**; mobile apps **Roadmap** |
| Consent prompts / session banner / idle policies | Banner **Have it (v1.0)**; consent **In v1.2** (item 20); idle-timeout policies **Roadmap** |

## B. Security / identity / trust

| Capability | EsoAgent |
|---|---|
| Per-device keys; expiring session codes | **Have it (v1.0)**; codes **In v1.2** (item 41) |
| 2FA | TOTP for owner logins **In v1.2** (item 7); push/WebAuthn/FIDO2: **Roadmap** |
| SSO SAML/OIDC + SCIM | **Roadmap** |
| Short-lived certificates | Grant tokens are short-lived signed grants **In v1.2**; SSH/X.509 CA: **Roadmap** |
| Credential vault + injection | **Roadmap** |
| Device approval, device certificates, pinned TLS | **Have it (v1.0)** |
| Device posture (patch/disk/cert state) | Health/doctor signals **In v1.2** (item 15); AV/EDR attestation: **Roadmap** |
| Conditional access / JIT access | JIT via scoped expiring grants **In v1.2**; posture/geo conditions: **Roadmap** |
| Moderated sessions | **Roadmap** (consent gate is the attended form) |
| Granular per-capability permissions | **In v1.2** (items 25 + 41); multi-operator roles: **Roadmap** |
| Audit logs + export + alert webhooks | **Have it (v1.0)** device audit; unified timeline + JSON/CSV export + signed alert webhooks **In v1.2** (items 3/16/23); SIEM streaming: **Roadmap** |
| IP allowlists | Agent binds tailnet + loopback only by design (v1.0); configurable allowlist: **Roadmap** |
| E2E content encryption (relay cannot read) | **Roadmap** — today the relay cannot *forge* (signed), and stream content transits the owner's own server over TLS; stated plainly in README |
| Compliance certifications (SOC 2 / HIPAA / FedRAMP) | **Roadmap** — organizational process, not a feature; the product is self-hosted |
| Zero-trust signed execution | **Have it (v1.0)** ★ |
| Keystroke logging / silent session-content inspection | **Refused** — design law: audit records actions and metadata; chat content and keystrokes are never logged |
| Silently bypassing OS security prompts | **Refused** — consent and OS prompts are respected by design |

## C. Networking

| Capability | EsoAgent |
|---|---|
| NAT traversal + relay fallback | **Have it (v1.0)** — WireGuard mesh with DERP-style relay fallback |
| Transports | WebSocket/WSS + WireGuard UDP **Have it (v1.0)**; QUIC/KCP: **Roadmap** |
| IPv6 / dual stack | **Roadmap** (supported by WireGuard; not yet verified end-to-end) |
| Subnet routing + route approval | **Have it (v1.0)**; HA route pairs: **Roadmap** |
| Exit nodes | **In v1.2** (item 21; Windows NAT is manual — see V12-REPORT) |
| Split tunnelling | **Have it (v1.0)** — mesh routes are additive by default |
| MagicDNS | **Have it (v1.0)** — name resolution + hosts sync; split DNS / filtering: **Roadmap** |
| Layer-2 bridging / multicast | **Roadmap** |
| Public exposure (Funnel/ngrok-class) + edge auth | **Roadmap** |
| P2P direct data path | **Have it (v1.0)** — mesh peers connect directly when the network allows |
| Port forwarding: local + dynamic (SOCKS5), TCP + UDP | **In v1.2** (items 5/24); remote forwarding: **Roadmap** |
| Site-to-site gateway mode | **Roadmap** (subnet routing both ends covers many cases today) |
| Kubernetes operator / API proxy | **Roadmap** |
| Self-hostable relay + coordinator; air-gapped operation | **Have it (v1.0)** |

## D. Management / fleet / platform

| Capability | EsoAgent |
|---|---|
| Grouping, online status, device registry | **Have it (v1.0)** |
| Bulk actions: scripts, patch, deploy, reboot | **In v1.2** (items 8/13/22); reboot **Have it (v1.0)** |
| Unattended-access policies | Per-device consent + capability policies **In v1.2**; time windows / per-group policy: **Roadmap** |
| Deployment | One-command installers **Have it (v1.0)**; MSI/GPO/MDM, QR enrol, client generator: **Roadmap** |
| Branding | Own brand set (golden wing) **In v1.2** (item 43); whitelabel generator: **Roadmap** |
| API + CLI parity; webhooks | **Have it (v1.0–v1.1)**; alert webhooks **In v1.2**; Terraform provider: **Roadmap** |
| Integrations (PSA / chat / SIEM / IdP) | **Roadmap** — webhooks are the bridge today |
| Reporting | Activity + health + export **In v1.2**; scheduled reports: **Roadmap** |
| Multi-tenancy (MSP) | **Roadmap** |
| Cross-platform honesty | Windows + Linux heads **Have it (v1.0)**; macOS head: **Roadmap** |
| Licensing shape | Self-hosted, no per-seat metering — a product decision (v1.0), not a tier |

## E. Terminal plane

| Capability | EsoAgent |
|---|---|
| Shell + file subsystems | **Have it (v1.0)** |
| Forwarding suite | **In v1.2** (local/dynamic, TCP/UDP); remote forwarding: **Roadmap** |
| Jump host | **Have it (v1.0)** — the fleet relay is the jump; streams joined it **In v1.2** |
| Persistent sessions | **In v1.2** (named shell sessions, item 26) + durable jobs **Have it (v1.1)** |
| Session recording (terminal) | Shell output ring **In v1.2**; full typescript export: **Roadmap** |
| Certificates instead of key sprawl | Device ECDSA identity + signed commands **Have it (v1.0)**; SSH certs: **Roadmap** |
| Script/snippet library | **In v1.2** (item 13, fleet script library with audit) |
| known_hosts at scale | Replaced by design — central device registry + certificate pinning **Have it (v1.0)** |
| X11 forwarding | **Roadmap** (any X server is reachable through a TCP tunnel today; not a named feature) |
| DB / K8s / app brokering | **Roadmap** — app bridges (below) are the adjacent system |

## F. Operator plane (beyond the census's frame)

| Capability | EsoAgent |
|---|---|
| One-call diagnostics ("what's the problem") | **In v1.2** (item 30) |
| Deep telemetry + threshold events | **In v1.2** (item 29) |
| Crash watch (WER / coredump) | **In v1.2** (item 31) |
| App bridges for creative/dev tools (Blender, Unreal, Unity, Godot, GIMP…) | **In v1.2** (items 32/38) |
| Universal workbench — any scriptable program registers by config | **In v1.2** (item 38) |
| Polyglot project detection + structured build/test results | **In v1.2** (item 40) |
| Structured-data native surfaces (typed JSON everywhere; `--json` CLI) | **In v1.2** (item 40) |
| Human-language UI (AR/EN, resource-based) | **In v1.2** (item 17); more languages = a resource file |
| OS patch management | **In v1.2** (item 22) |
| Device scheduled tasks (the device's own cron) | **In v1.2** (item 14) |
| Health dashboard from doctor snapshots | **In v1.2** (item 15) |
| Fleet-wide signed update rollout with rollback | **In v1.2** (item 8) |

---

*Refused, also:* reusing competitor code (the census informed features,
never code — the product is original) and any feature whose purpose is
to watch the person at the device rather than serve the device's owner.
