# EsoAgent — Competitive Study: the whole remote-access field, and how we beat it

**Date:** 2026-11-20 · **Status:** decision document — every finding lands as a move, not trivia.
**Method:** 12 parallel research tracks (one per competitor, one cross-cutting security-incidents
track, one performance-evidence track), each written from primary/reputable sources with
per-claim URLs. Full profiles live in `~/workspace/.comp-study/` (`teamviewer.md`, `anydesk.md`,
`rustdesk.md`, `tailscale.md`, `meshcentral.md`, `splashtop.md`, `parsec.md`,
`chrome-remote-desktop.md`, `realvnc.md`, `ssh.md`, `security-incidents.md`,
`performance-evidence.md`). EsoAgent's current state is taken from this repo's own audited
docs (README, FEATURE-MATRIX.md, BENCHMARKS.md, UI-OVERHAUL.md, MESH-EMBED-SPIKE.md,
LAUNCH-REPORT.md) — including the unflattering parts.

**Evidence discipline (used throughout):** `[vendor claim]` = the maker's own marketing,
unverified. `[measured — source]` = an independent measurement with a stated method.
Where neither exists, this document says so — the absences are findings, not gaps in the research.
One track (DWService/HelpWire) returned no usable profile in this round; it is covered only
where other tracks measured it (see §2.11) and is not profiled from memory.

**Effort scale (solo-dev):** S ≈ days, M ≈ 2–4 weeks, L ≈ a month+ or a phase.

---

## 1. EsoAgent's honest baseline (2026-11-20, v1.2.x)

What we actually have, measured against the field below:

- **One self-hosted product:** agent + fleet server + desktop app + web operator panel + `eso`
  CLI over one backend, one device registry, one audit timeline. No vendor cloud exists.
- **Trust model nobody else in this study ships as the default:** every operator action is
  ECDSA-signed by the operator key and **verified by each device itself**; the fleet server
  relays but holds no signing key and **cannot forge a command**. Enrolment is owner-approved
  (device code `XXXX-XXXX`), never self-joining. Owner holds a kill switch over the operator.
- **Shell parity with SSH:** unrestricted exec at service privilege (LocalSystem/root),
  persistent shell sessions, durable jobs, chunked resumable SHA-256-verified file transfer,
  TCP/UDP/SOCKS5 tunnels, Wake-on-LAN, script library, patch management, device schedules.
- **Measured (BENCHMARKS.md, loopback on one VM, stated method):** exec round-trip median
  **5 ms direct / 14 ms via fleet relay**; file transfer 21.7 MiB/s up / 102.3 MiB/s down
  direct, 4.5 / 11.7 MiB/s relayed; a structural "what's on screen" answer costs **665 bytes
  vs 49,818 bytes** for one pixel frame (~75×).
- **Install:** one Inno installer, **124,427,842 B (~124 MB, v1.2.3)**, zero prerequisites —
  with one honest defect: the Windows mesh tunnel today shells out to a separately installed
  WireGuard app. The embedded engine (wireguard-go sidecar, MIT + `wintun.dll` under the
  Prebuilt Binaries License, prototype proven with packets passing — MESH-EMBED-SPIKE.md) is
  in implementation; its acceptance test is *mesh up on a machine with no WireGuard installed*.
- **Desktop:** adaptive JPEG + frame-diffing, honestly rated **5–15 fps admin-grade**.
  Hardware H.264 is roadmap (phase 3). This is our weakest dimension against the field (§3).
- **UX:** web panels rebuilt light (Phase 0+1, shipped in v1.2.4); the app's Phase 2
  (TeamViewer-style home, device code at 26 px, connect-by-code, 3-step pairing with zero
  hashes typed, 1-click sessions) is done in source and ships next release. Session chrome
  (fullscreen/scaling/stats, file-transfer queue UI) is Phase 3, not yet built.
- **Known gaps vs the field:** no macOS head, no mobile apps, no remote printing, no session
  audio, no multi-technician handover, no virtual displays/dynamic resize, stream content is
  TLS-protected through the owner's own server but not yet end-to-end encrypted at the
  application layer, no SSO/SCIM, no MSI/GPO package yet, installer is ~15× AnyDesk's size.
- **Price shape:** self-hosted, **no per-seat metering, no tiers** — a product decision, and
  (see §2) a direct attack on the market's loudest complaint class.

---

## 2. Competitor profiles (compact; full sourced profiles in `.comp-study/`)

### 2.1 TeamViewer — the incumbent
- **Install/UX:** full client 80.3 MB, Host 41.2 MB [measured — download listings, performance
  track §6]. Mature apps, deep enterprise deployment (MSI/policies), Tensor for management.
- **Architecture:** entirely cloud-brokered through TeamViewer's router network. On-prem
  session routing exists only as Tensor + Hybrid Conditional Access add-on: initial setup
  still via TeamViewer cloud, Docker routers (4-core/8 GB min), and a **mandatory paid
  Professional Services package** (teamviewer.com knowledge base).
- **Performance:** publishes **no** official latency/FPS/transfer benchmarks; the only
  multi-product camera test including it is from 2014 and was run by AnyDesk (§5).
- **Reliability/pain:** free-tier "commercial use suspected" false positives cut sessions to
  5 minutes; unblocking is a manual form (up to 10 IDs, review "within 72 hours");
  uninstalling doesn't clear the flag.
- **Security:** June 2024 — APT29/Midnight Blizzard (vendor attribution) entered the corporate
  IT environment via one standard employee's credentials and copied the employee directory
  incl. encrypted passwords; vendor states product/customer data untouched `[vendor claim]`
  (teamviewer.com bulletin TV-2024-1005; BleepingComputer). CVE-2026-111585 (CVSS 8.8): a
  malicious *host* can execute code on the connecting *technician's* viewer.
- **Pricing:** Premium = 15 licensed users but **1 concurrent channel** ($120.90/mo billed
  annually); Corporate 30 users / 3 channels ($245.90/mo). EULA requires cancellation
  **≥28 days before renewal** or a full year is owed; BBB records 190 complaints in 3 years
  with a recurring disputed-renewal → collections pattern.

### 2.2 AnyDesk — the lightweight
- **Install/UX:** the bar: **8.2 MB single EXE**, runs portable with no install/admin,
  ID-based session in seconds [measured — performance track §6]. macOS DMG is 25.9–26.7 MB.
- **Architecture:** Solo/Standard/Advanced all run on AnyDesk's global cloud. Full on-premises
  (rendezvous + relay + registry in-house, offline-capable) is **Ultimate tier only,
  custom-priced, sales-gated** (support.anydesk.com/docs/on-premises).
- **Performance:** DeskRT claims <16 ms LAN / 60 fps / usable at 100 kb/s `[vendor claim]`;
  no lab-grade independent benchmark of current versions exists — its case rests on vendor
  claims plus a self-run 2014 test and old composite scores.
- **Security:** **February 2024: production systems compromised; source code and private
  code-signing keys reported stolen** (BleepingComputer, via sources; AnyDesk confirmed the
  compromise, revoked all security certificates, force-reset every portal password, rotated
  the signing cert in v8.0.8, after a 4-day login shutdown first labelled "maintenance").
  Repeated pre-auth RCEs in the closed DeskRT protocol: CVE-2026-109390 "AnyPwn" (9.8,
  unauthenticated, public exploit Oct 2026; the fix shipped changelogged only as a crash fix)
  and CVE-2025-27918 (9.8).
- **Trust/abuse:** the 9-digit-ID + password model is the tech-support-scam standard
  (RBI 2019 and SBI 2021 warnings; FBI advisory linking AnyDesk to AvosLocker, 2022).
- **Pricing:** connection-based licensing — Standard's $49.90/mo buys **one** concurrent
  connection for a 20-user team (extras $29.90 each); Solo/Standard prices rose ~94%/~118%
  (2024→2026); missed 30-day cancellation deadlines end in debt collection (documented
  Trustpilot cases). MSI/GPO, REST API, audit logs, SSO are sliced across Advanced/Ultimate.

### 2.3 RustDesk — the open-source rival
- **Install/architecture:** client 25.0 MB EXE (v1.5.3) [measured — performance track §6].
  Self-hosting = **two daemons (hbbs + hbbr), 6 ports (21114–21119)**, `--net=host`
  Linux-only, hbbr ships with an empty key unless started with `-k _`, and every OSS client
  must be manually re-pointed to the server + key (rustdesk.com/docs).
- **The fleet is paywalled; the session is free:** OSS has no web console, no synced address
  book, no device grouping, no audit log, no API — all Server Pro. Even Pro unlocks the web
  client only if `(users × 10) + devices ≥ 400`, which the standard Basic plan
  (10×10+100 = 200) does **not** meet (rustdesk.com/pricing; Trustpilot "no refunds" reports).
- **Performance:** reputation with **zero published evidence** — the most careful comparison
  found states "We haven't run a controlled benchmark, so there are no numbers here"
  (meetrix.io). Server idle footprint is genuinely tiny: **14.5 MiB RAM** for hbbs+hbbr
  [measured — ComputingForGeeks, same-VM comparison].
- **Security:** a 2026 CVE batch that reads as a design sweep (CVE-2026-30783–30798 cluster:
  pseudo-encrypted deployment config, zero-click `rustdesk://config/` infrastructure hijack,
  zero-click password overwrite), on top of unauthenticated hbbs RCEs (CVE-2022-41082, 9.8;
  CVE-2025-31186, 8.1) and the disputed WDKTestCert-in-trust-store CVE-2024-25140. Signed
  update-metadata verification only arrived in 1.5.1 (2026-11-01).
- **Trust:** scam abuse forced a voluntary Google Play exit (2023) and IdP-gating of the
  public server — a burden a fleet-only, enrolment-by-code product never carries.

### 2.4 Tailscale (+ Headscale) — the mesh incumbent
- **What it is:** a superb WireGuard control plane — and **no remote desktop at all**. Its
  own recipes tell users to run a separate RDP/VNC/RustDesk client over the tailnet.
  Tailscale SSH is the hybrid: identity-based SSH with check mode and session recording.
- **Install/UX:** ~108 MB Windows installer (stub/full variants) [measured — performance
  track §6]; "it just works" reputation; ACL policy syntax and MagicDNS quirks are the
  recurring confusion. Headscale (self-hosted control plane) has no GUI, single-tailnet
  scope, and feature lag.
- **Architecture/trust:** the **closed coordination server holds the complete fleet map**
  (names, owners/tags, tailnet IPs, endpoints, last-seen, policy) and decides membership.
  Tailscale's own Tailnet Lock material admits a malicious/compelled server could enrol a
  secretly-added node peers would encrypt to; Tailnet Lock mitigates enrolment only, and is
  mutually exclusive with Device Approval.
- **Performance:** direct WireGuard is fast; **DERP relaying collapses throughput** —
  268 → 42 Mbit/s and +118 ms RTT in one controlled test [measured — DoWithSudo]; 18 vs
  ~600 Mbit/s [measured — GetMind, Oct 2026]; field data ~4–20 Mbps relayed vs ~200 direct.
  Root cause: DERP rides a single outer TCP connection.
- **Security:** **Tailworm (Oct 2026)** — 11 findings in v1.104.2: the root-running
  `tailscaled` SSH server forwarded Unix sockets as root, letting any policy-permitted SSH
  session reach the Docker/systemd sockets, read `/etc/shadow`, capture enrolment keys
  (fixed v1.104.4; BleepingComputer/The Hacker News). On Headscale the forwarding couldn't
  even be disabled.
- **Pricing:** per-seat — Standard $8/user/mo, Premium $18/user/mo, billed per assigned seat
  whether used or not; network flow logs, log streaming, advanced SSH, just-in-time access
  are Premium-only; tagged resources (subnet routers etc.) bill $1/mo each beyond 50;
  free tier is non-commercial only.

### 2.5 MeshCentral — the closest architectural analog
- **Install:** the anti-EsoAgent. Official step 1: "ensure Node.js and NPM are installed"
  (Node 22+ advised), then a dedicated user, `npm install meshcentral`, hand-set DNS +
  `--cert`, a systemd unit, port privileges. Independent accounting: "several hours to a
  day, plus DNS and TLS"; first run is LAN-only with a self-signed cert and browser warnings
  (docs.meshcentral.com; openmsp.ai).
- **Features:** the breadth benchmark — desktop, terminal, files, recording, Intel AMT,
  Wake-on-LAN, groups, audit, scripting. If a feature exists in this market, it is probably
  here — inside a console reviewers unanimously call "dated and dense… built by engineers,
  for engineers." No native operator app; Android agent is view-only (no input control).
- **Performance:** desktop is a 32×32-tile JPEG/WebP protocol relayed through the server;
  WebRTC P2P exists but is off by default. An independent transport study classifies it
  "VNC-class"; one WAN user reported ~1 fps / 3 s echo before fixing WebRTC config. **No
  measured latency/fps numbers exist anywhere.** Idle server: **284 MiB RSS** [measured —
  ComputingForGeeks].
- **Security:** **October 2026: an active exploit was taking over servers *and their fleets*.**
  Release 1.3.1 (23 Oct 2026) says verbatim the upgrade "SHOULD HOPEFULLY [HAVE] PATCHED THE
  EXPLOIT"; still no CVE/advisory describing the flaw. Behind it, a chain: CVE-2023-51837
  (9.8, missing cert validation), CVE-2024-26135 (8.8, cross-site WebSocket hijack →
  session-key leak → forge tokens as any user), CVE-2026-66420 (that fix bypassed precisely
  in the default self-signed configuration), GHSA-c7hr-448w-65px (rogue agent → stored XSS
  in the admin's browser). Kaspersky's Operation ShadowCloud documents state actors using
  the legitimate MeshAgent as an initial foothold.
- **Governance:** Intel ended sponsorship Nov 2022; the author (now at Microsoft) declared
  it "no longer my priority." Maintenance continues (14 releases Jan–Oct 2026) but rests on
  two people, with no vendor, no SLA, no commercial support at any price.

### 2.6 Splashtop — the value/performance commercial
- **Performance:** markets up to 240 fps — **Performance tier only ($149/user/yr)**; the
  Business app's own setting caps at "Ultra High = 60 FPS"; a review citing provider lab
  data puts 4K at 40 fps, contradicting the 2026 "sustained 60 FPS at 4K" press release.
  **No independent latency measurement in ms exists anywhere found** [vendor claims only].
- **Reliability:** its cloud is a fleet-wide kill switch — login/lookup outages roughly
  **every ~40 days** (185+ collected since 2017; e.g. 2026-02-26 global+EU login outage;
  2025-06-23 ~4.4 h). When lookup is down, *no new sessions at all*; no local fallback.
- **Security:** six years of one pattern — local → SYSTEM privesc in the installer/updater:
  CVE-2020-12431, CVE-2021-42712, CVE-2022-50693 (8.4, unquoted service path),
  CVE-2023-3181, CVE-2024-42050–42053. Silent progressive auto-update (3% → ~100% in
  3 weeks) shipped the broken Streamer 3.8.2.0 whose vendor fix is hand-creating an
  `updater.disable` file. Abuse: CISA's Black Basta advisory lists Splashtop for lateral
  movement; MITRE ATT&CK notes Qilin using it; blocking rogue remote tools is a *paid
  add-on* (Splashtop Shield).
- **Pricing/fragmentation:** five+ product lines, three licensing axes; SSO, SIEM, API,
  cloud recording, on-prem are all quote-only Enterprise; Personal is LAN-only and "working
  from home counts as commercial use"; annual-only, no refunds. Linux requires GUI + X11 —
  Wayland/headless/Ubuntu Server officially unsupported, and you can't initiate *from* Linux.

### 2.7 Parsec — the latency king
- **Performance (the one real crown in this study):** the "7 ms" figure is Parsec's own
  *added-pipeline* claim with no published rig `[vendor claim]`. The one proper independent
  lab test (high-speed camera, 500 samples/config, RTX 3080 → 144 Hz client, May 2026)
  measured **Parsec 10–16 ms median input-to-photon on LAN (p95 18–28 ms)** — and
  **Moonlight beat it at 8–12 ms** [measured — TenvoAI]. Parsec is beatable, but only on a
  published, method-stated harness.
- **Admin plane: absent at any price.** No native file transfer, no session recording, no
  remote execution (its own pricing matrix lists none). The relay is paywalled *and*
  self-hosted: P2P-only below Enterprise ($45/user/mo, annual only), so two CGNAT endpoints
  can simply fail to connect; the High Performance Relay is Enterprise-only and the customer
  runs it themselves.
- **Walls:** hosting is Windows/macOS only (no Linux host, no iOS client); Free and Warp
  ($8.33/mo) licences are **non-commercial** — commercial use starts at Teams $30/user/mo;
  Guest Access costs +$25 per invitation (≤5 days); Teams audit logs cover only 7 days.
  (Correction to a common belief: Parsec for Teams was *not* discontinued — Arcade was, 2023.)
- **Security:** every session is brokered by Unity's backend — no air-gap, no self-hosted
  control plane. Both Unity-Parsec CVEs are the same pattern — local privesc to SYSTEM via
  the privileged service trusting user-controlled state: CVE-2023-37250 (7.0) and
  CVE-2026-54424 (8.4, public PoC).

### 2.8 Chrome Remote Desktop — the free baseline
- **What it is:** a 3-click first session and nothing else. **No management plane at all** —
  no fleet console, no groups, no RBAC, no audit log, no session recording; the whole
  Workspace admin surface is an On/Off switch per OU. Identity ceiling: a consumer Google
  account + a **6-digit PIN**. Coordination cannot be self-hosted.
- **Performance:** in the one real comparative test found (same Win11 VM, 10 Mbps rural
  mobile, >600 ms latency), CRD was "soft" with typing lag, and an 80 MB file took
  **~8 min vs 1:48 (RustDesk) and 1:03 (RDP over Tailscale)** [measured — MakeUseOf,
  Oct 2026]. Google publishes no performance numbers.
- **Reliability:** its signature failure is unattended hosts silently dropping offline until
  locally re-authenticated ("defeats the purpose of remote administration" — Spiceworks);
  in Aug 2026 a pushed host build left hosts stuck on "Starting," Google confirmed a
  server-side cause, and auto-update re-broke manually rolled-back hosts.
- **Abuse:** attackers install CRD bound to *attacker-controlled Google accounts* as
  persistence (Huntress reported a "definitive uptick," Oct 2025, incl. pre-Akira-ransomware
  deployment; Kimsuky campaigns) — invisible, because there is no console or audit trail.
  Google deprecated the iOS native app (Sep 2025) in favour of a PWA users call inadequate.

### 2.9 RealVNC — the legacy incumbent
- **Exposure:** VNC's direct mode *is* port 5900. GreyNoise measured **17.4 M VNC sessions
  in one week** (Apr 2026), the #3 most-targeted service globally; Cyble/The Register found
  8,000+ passwordless endpoints and >640k exposed VNC hosts. RealVNC escapes only by renting
  its cloud.
- **Performance:** RFB streams pixel rectangles over TCP; independent explainers put VNC's
  practical bandwidth floor at ~2 Mbps vs RDP's ~100 kbps. RealVNC counters with vendor
  claims only ("adaptive quality," an unlinked "ranked first for low latency"). **No
  controlled latency/FPS/bandwidth numbers for RealVNC Connect were found.**
- **Pricing pain (its loudest wound):** multiple 2026 reviewers report auto-renewals at
  **2–5× the prior year** (~$200 → $714; $276 → $594) without clear notice, a 30-day
  cancellation rule, refund refusals. The model churned three times (perpetual → per-device
  → per-concurrent-session, Nov 2024); the free Home plan was retired (Jun 2024) and Lite
  cut to 3 devices/1 user *without* file transfer or printing.
- **Tier-gating of basics:** direct LAN connections and audit logs require Premium; session
  recording and API require Plus; SSO and offline licensing require Enterprise. Its API is
  management-only — **there is no command plane at all**; even audio is a paid, one-way
  proprietary extension (RFB has no audio message by design).

### 2.10 SSH — the protocol incumbent (full parity table in §4)
- **The bar:** one-RTT text echo; `ssh host 'cmd'` scriptable exec; ControlMaster
  multiplexing; ships with every OS ("installer size: 0, already there"). Bulk transfer is
  its measurable weakness: fixed channel windows let HPN-SSH post up to ~1000% throughput
  gains (+59% vs OpenSSH 9.4) [measured — PSC/HPN-SSH].
- **No fleet layer exists:** inventory = hand-edited `~/.ssh/config`; default logging doesn't
  record which key was used, let alone commands; no NAT traversal (impossible under CGNAT);
  no desktop. The market pays Termius $120/yr and SecureCRT $119 purely for host
  management/sync around the free protocol.
- **Identity is the structural disease:** keys never expire, never revoke centrally, sprawl
  across every `authorized_keys`; host trust is TOFU everyone blindly accepts. SSH
  certificates — the proper fix — have existed ~15 years and remain a minority practice,
  because adopting them means building the fleet layer SSH lacks.
- **Windows traps (the checklist our installer must provably avoid):** admin keys are read
  from `C:\ProgramData\ssh\administrators_authorized_keys`, not `~/.ssh/`; the file is
  refused if its ACL includes anyone beyond SYSTEM/Administrators; the Server-2025 firewall
  rule covers only the Private profile — all surfacing as a bare `Permission denied`.
- **Security record:** regreSSHion (CVE-2024-6387) — unauthenticated root RCE in sshd
  8.5p1–9.7p1, 14M+ exposed instances at disclosure; **over a year later fewer than 500k of
  ~20M exposed servers ran the fixed 9.8+**, because updates ride each OS's package manager
  (Qualys; Network World/runZero). Honeypots see the first brute-force attempt in <60 s.

### 2.11 DWService / HelpWire — coverage note
No usable profile was returned for this track in this study round, and this document does
not profile them from memory. What the other tracks established: DWService has **no
independent measurement of any metric** (performance track §8), and its per-application
sharing model is noted in our FEATURE-MATRIX census. A sourced profile is a follow-up item,
not a silent omission.

---

## 3. The BEAT-THEM MATRIX

Rows are the dimensions that decide purchases. For each: who leads today (with the evidence),
where EsoAgent stands, and the specific move that beats the leader.

| Dimension | Leader today (evidence) | EsoAgent now | The move that beats the leader | Effort |
|---|---|---|---|---|
| **Install & onboarding** | **AnyDesk** — 8.2 MB portable EXE, no install/admin, session in seconds [measured]. Honourable mention: SSH, "already there." Worst: MeshCentral (Node.js prerequisite + hours). | One installer, zero prerequisites — but **124 MB**, and the Windows mesh still leans on an external WireGuard install until the embedded engine ships. | (1) Ship the embedded mesh sidecar — closes the last external install, forever. (2) Installer diet (trim/single-file .NET publish, compression) toward **<60 MB**, then publish the timed download→install→first-session stopwatch test nobody else publishes. | M (sidecar, in progress) · M (diet) · S (publish timings) |
| **UI ease** | **TeamViewer/AnyDesk** (mature native apps); **CRD** owns the 3-click first session. MeshCentral's console is the cautionary tale ("dated and dense"). | App Phase 2 done in source (home with 26 px device code, connect-by-code, 3-step pairing, 1-click sessions); web panels rebuilt light; **session chrome and file-queue UI not yet built** (Phase 3). | Ship Phase 3 session chrome (fullscreen, scaling modes, live stats, transfer queue with progress/cancel) and hold the click budget: ≤1 click to a known device, ≤3 steps to pair a new one, verified by counted flows in UI-OVERHAUL.md's convention. | M |
| **Session smoothness** | **Parsec** — 10–16 ms median input-to-photon LAN [measured — TenvoAI]; Moonlight 8–12 ms is the real ceiling reference. Splashtop's 240 fps is a paywalled `[vendor claim]` with zero independent ms data. | **Our weakest row, honestly:** adaptive JPEG, 5–15 fps admin-grade; no hardware encode yet. | Hardware H.264 encode (Media Foundation/VAAPI) with the existing codec-negotiated channel, quality-floored adaptation kept. Publish input-to-photon on the §6 harness — even mid-pack measured numbers beat everyone's unmeasured claims. | L (encode) · S/M (harness) |
| **Features breadth** | **MeshCentral** (AMT, recording, terminal, files, WoL — the census union) with TeamViewer close behind on polish. RustDesk OSS is the floor: no console/address book/audit/API at all. | Already a superset of RustDesk OSS *and* its Pro tier on management (console, audit, API, script library, patch mgmt, schedules, tunnels, recording all in the one product). Missing vs the top: remote printing, session audio, multi-tech handover, virtual displays. | Close the named four in order of admin value: printing (M), audio (M), handover (L), virtual displays (L). Never paywall a management feature — RustDesk's `(users×10)+devices ≥ 400` web-client formula is the anti-pattern to cite. | M–L |
| **Architecture & self-hosting** | **EsoAgent leads this row today.** Full self-hosting incl. relay is our default; elsewhere it is AnyDesk's top custom-priced tier, TeamViewer's Tensor add-on + paid services, Tailscale's GUI-less Headscale fork, or Parsec's Enterprise-only self-run relay. | One server the owner runs; devices dial out only; mesh = WireGuard P2P with UDP relay fallback (DERP's TCP-relay tax avoided by design); server **cannot forge commands**. | Extend the lead: end-to-end stream content encryption (relay becomes blind to content, not just unable to forge) + signed mesh-record distribution (our Tailnet-Lock analog). Publish the architecture proof: "steal our server, you still can't command the fleet." | M |
| **Reliability** | Nobody leads. Splashtop's cloud kills all new sessions ~every 40 days; CRD hosts drop auth silently and a pushed build re-broke rolled-back hosts; TeamViewer/AnyDesk uptime is a vendor dependency. | No vendor outage can exist — but the owner's single fleet server is a single point of failure, and update regressions are prevented only by our staged rollout + health check + auto-rollback (already designed in). | Fleet-server standby/HA pair + offline command queue surfacing (durable jobs exist; make the queue visible). Publish relay capacity/ soak numbers with the benchmark suite. | M |
| **Security** | **Split crown:** SSH for battle-tested crypto/privsep; **EsoAgent for trust model** (signed commands, device approval, owner kill switch — none of the cloud products can say "our server cannot command your fleet"). The field's record: AnyDesk signing keys stolen (2024), TeamViewer APT29 (2024), MeshCentral fleet-takeover exploit (2026), Tailworm root-via-SSH (2026), regreSSHion patch lag. | Signed execution since v1.0; TOTP owner 2FA; pinned TLS; signed updates with rollback; unified audit. Unproven in public: no external audit, no published threat model. | (1) Privileged-broker minimisation audit — no SYSTEM code path touches user-writable state (the market's #1 CVE class). (2) Publish the threat model + the §5 rules as tested invariants, incl. regression tests for removed safety checks (the regreSSHion lesson). (3) Step-up confirmation for sensitive commands (Tailscale check-mode pattern). | S–M |
| **Privacy** | **EsoAgent leads.** No telemetry, no third-party servers, metadata lives on the owner's box. Contrast: Tailscale's coordination server holds the complete fleet map; TeamViewer/AnyDesk clouds see session metadata by design; CRD binds hosts to a consumer Google account. | As stated — this row is ours to lose. | Publish a one-page data-flow diagram (what exists, where it lives, what the relay can and cannot see) and keep the no-telemetry law in the release checklist. | S |
| **Price** | **CRD/DWService** on sticker (free, featureless); **RustDesk OSS** on capability-per-dollar for a bare session. Everyone else monetises pain: per-seat (Tailscale $8–18/user/mo), per-connection (AnyDesk, TeamViewer channels), renewal ambush (RealVNC 2–5×). | **No per-seat metering, no tiers, no renewals** — self-hosted, owner-run. The honest cost is the owner's server + admin time, which we should state, not hide. | Publish a TCO page: 10-device fleet, 3 years, EsoAgent vs TeamViewer Corporate vs AnyDesk Standard vs Tailscale Premium — licence + hosting + the paywalled features priced in. | S |
| **Terminal plane (the SSH row)** | **SSH** for feel (one-RTT echo) and ubiquity; it has no fleet layer at all (§2.10, §4). | Unrestricted shell at service privilege, persistent sessions, exec median 5 ms direct / 14 ms relayed [measured, loopback]. | Publish WAN terminal-latency parity numbers + roaming reconnect (survive network change without session death — the Mosh lesson SSH never fixed). | S (measure) · M (roaming) |

---

## 4. SSH vs EsoAgent — capability-parity table

SSH is the ambition's hardest target because its core is genuinely excellent. Merged from the
SSH track's parity table (`.comp-study/ssh.md`) with EsoAgent's audited state
(FEATURE-MATRIX.md §E, README, BENCHMARKS.md). Verdicts: **Won** / **Matched** / **Gap**.

| Capability | SSH's state | EsoAgent's state | Verdict + the move to close it |
|---|---|---|---|
| **Terminal / exec** | Gold standard: ~zero payload, one-RTT echo, scriptable exec, ControlMaster multiplexing. Weaknesses: per-connection setup cost, no roaming, no local echo. | Unrestricted shell at service privilege (not a subset — README states it as the anchor), WebSocket streaming exec, persistent named shell sessions, durable jobs. Exec round-trip median 5 ms direct / 14 ms relay [measured, loopback]. | **Matched on capability; unproven on WAN feel.** Move: publish echo-latency parity (LAN + shaped WAN, §6) and add session roaming across network changes. (S measure · M roaming) |
| **File transfer** | `sftp`/`scp` CLI only; fixed channel windows cap bulk throughput (HPN-SSH: up to ~1000% headroom, +59% vs 9.4) [measured]. GUI/queue/resume = third-party tools. | Chunked upload/download with per-file SHA-256 verification and resume offsets; app file manager (v1.2). Transfer **queue UI with progress/cancel is Phase 3**, not built. | **Matched on protocol, gap on UI.** Move: ship the Phase 3 transfer queue, then publish throughput vs `scp` on LAN + high-RTT paths targeting HPN-class numbers. (M · S) |
| **Tunnels / port-forwarding** | Complete on paper: `-L` / `-R` / `-D` SOCKS / `-w` TUN, ProxyJump — all manual, per-session, CLI/config only, no persistence, no UI, no policy. | TCP + UDP + dynamic SOCKS5 tunnels over direct and fleet channels (v1.2). **Remote (reverse) forwarding is roadmap**; tunnel management UI is Phase 4. The WireGuard mesh already replaces the common ProxyJump case with zero bastions. | **Gap (one direction + UI).** Move: remote forwarding (M), then a visual tunnel list with live state and per-device policy (M). |
| **Multi-host management** | None. Inventory = hand-edited `~/.ssh/config`; parallel exec = Ansible/MobaXterm bolt-ons; no presence or health. Users pay Termius $120/yr for this layer alone. | Live registry: presence, OS, version, groups, `--group`/`--all` fan-out with per-device aggregated results (failures never silently dropped), health dashboard, unified timeline. | **Won.** This row is our home ground — keep it visibly ahead (fleet search, saved views). (S polish) |
| **Audit** | Default: auth lines only — not even which key (needs VERBOSE), never commands. Recordings are per-host bolt-ons stored where root can delete them. | Every command audit-logged on device + fleet timeline (who/what/when/result), exportable JSON/CSV; desktop session recording (`.esorec`); shell output ring. Full terminal typescript export is roadmap; recordings *can* be governed only by the owner tier. | **Won, one notch left.** Move: typescript export + an owner-set "refuse unrecorded sessions" option (Tailscale SSH's `enforceRecorder` pattern). (S/M) |
| **NAT traversal** | None. Needs a public port (impossible under CGNAT), a router forward, or a hand-built bastion. Port 22 exposure is brute-forced within a minute of appearing. | Outbound-only agent connections (zero inbound ports), fleet rendezvous + relay, WireGuard mesh with relay fallback. Enrolment + signed command round-trip proven live on the owner's fleet (2026-11-20); mesh packet proof lands with the embedded engine. | **Won architecturally; final proof pending.** Move: complete the embedded-mesh acceptance run incl. a real CGNAT path, and say so with logs. (S once sidecar lands) |
| **Identity / keys** | Raw key pairs: manual distribution, no expiry, no central revocation, sprawl; TOFU host trust; certificates exist but are a minority practice; no 2FA, no device approval. | Device ECDSA identity + human device code issued at enrolment; operator-signed commands (no bearer key on devices to steal); owner-approved enrolment; one-click fleet-wide operator revocation (owner master control); TOTP owner 2FA. No `known_hosts` wall can exist. | **Won.** Remaining notch: step-up confirmation for sensitive commands (check-mode pattern). (S/M) |
| **Remote desktop** | None whatsoever. X11 forwarding ≠ desktop; Windows GUI admin is impossible. | Full desktop (DXGI/GDI on Windows, X11 on Linux), view/control modes, clipboard sync, multi-monitor enumeration, session banner + audit, relayed through the fleet when direct is impossible. Quality is admin-grade JPEG today. | **Won on existence; the real fight is quality** — users compare against RDP/TeamViewer/Parsec, not SSH. Move = the §3 smoothness row (H.264). (L) |

**What "surpassed SSH" honestly requires (all four, from the SSH track):**
1. Don't lose SSH's free-core advantage: no per-seat rent for basic fleet access, self-hostable
   server, no third-party account. *(Held — product law.)*
2. Interop humility: import `~/.ssh/config` hosts; offer an SSH-shaped exec surface so muscle
   memory transfers. *(Not built — backlog #14.)*
3. Win arguments with measurements, the way SSH's defenders do (`time ssh host true`):
   echo latency, file throughput vs `scp`/HPN-SSH, reconnect time, novice time-to-first-session
   on Windows vs the §2.10 trap list. *(Protocol in §6.)*
4. Architectural, not asserted, security: least-privilege agent + signed commands + fleet-push
   updates that make the regreSSHion patch-lag statistic impossible by construction.
   *(Mostly held; publish the proof.)*

---

## 5. Their pains we will never repeat — product rules

Each rule is a competitor's documented failure converted into an invariant. These belong in
the release checklist; a release that breaks one does not ship.

**Trust & security**
1. **The server can be stolen and still cannot command the fleet.** Kaseya's deployment
   function pushed REvil ransomware to ~800–1,500 downstream businesses (2021); MeshCentral's
   Oct 2026 exploit took servers *and their fleets*. Our signed-command separation is the
   answer — never dilute it for convenience. (security track §1.4, §3.1)
2. **Signing keys never live on a networked production/build system.** AnyDesk, Feb 2024:
   production compromise → all certificates revoked, every portal password force-reset,
   signing cert rotated. Our release key stays offline; updates verify-then-execute,
   atomically, or not at all.
3. **No SYSTEM component touches user-writable paths or unauthenticated local IPC.** This is
   the market's #1 repeated CVE class: Splashtop's six-year installer/updater privesc chain,
   Parsec's two service-trust privescs, AnyDesk CVE-2022-32450, Tailscale CVE-2022-41924 (9.6).
4. **Fail closed on every signature/TLS check; no test certificates in any trust store, ever.**
   RustDesk's WDKTestCert (CVE-2024-25140) and MeshCentral's missing cert validation
   (CVE-2023-51837, 9.8) are what "temporary" exceptions become.
5. **Security fixes ship labelled as security fixes.** AnyDesk's AnyPwn fix (9.8, pre-auth
   RCE) was changelogged as "a bug that could lead to a crash" for months. Never.
6. **Removed safety checks get regression tests.** regreSSHion (CVE-2024-6387) was a 2006 fix
   silently regressed in 2020 and shipped for four years. A deleted guard is a hostile diff.
7. **The product must be useless to scammers.** No public rendezvous, no portable
   run-and-join, no read-me-the-code enrolment — owner-approved enrolment only. (CISA
   AA23-025A documented portable RMM abuse in federal refund scams; AnyDesk/CRD are the
   scam standard precisely because anyone can enrol a victim's machine to a stranger's
   account.)
8. **Every enrolment is visible in one registry, forever.** CRD's attacker-account
   persistence is invisible because no console exists. Ours is the console.
9. **Audit cannot be disabled or deleted from the target device.** SSH's default (no key
   fingerprint, no commands) and per-host recordings root can erase are the anti-pattern.

**Reliability & updates**
10. **No cloud lookup can kill our sessions** — there is no vendor cloud. Splashtop's
    ~40-day outage cadence and CRD's server-side "stuck on Starting" are what renting your
    control plane costs. Our SPOF is the owner's server, which the owner can fix.
11. **Updates are staged, health-checked, and auto-rolled-back — never silent-progressive.**
    Splashtop's 3%→100% silent rollout shipped a broken Streamer; CRD's auto-update re-broke
    manually repaired hosts. Fleet-push with per-device results is also how the regreSSHion
    statistic (<500k of ~20M patched in a year) becomes impossible here.
12. **Relays must not silently tax.** DERP's single-TCP design costs 6–50× throughput and
    users are never told which path they're on. Our relay is UDP; every session and every
    benchmark logs direct vs relayed.

**Commerce & UX honesty**
13. **No "commercial use suspected" classifiers.** TeamViewer's and AnyDesk's free-tier
    policing manufactures hostile users (5-minute cut-offs, manual unblocking forms). We
    have no free tier to police — the product is the owner's.
14. **No renewal traps.** No ≥28-day cancellation clauses (TeamViewer EULA), no debt
    collectors (AnyDesk/Debtist cases), no 2–5× silent renewal hikes (RealVNC). No per-seat
    metering, no per-connection channels, no feature paywall formulas (RustDesk's
    `(users×10)+devices ≥ 400`).
15. **No prerequisites.** MeshCentral step 1 is "install Node.js." Our law stands: one
    installer, nothing external — the embedded mesh engine closes the last exception.
16. **No silent capability ceilings.** If a path is relayed, a quality floor is hit, or a
    feature is unavailable on a platform (our own Wayland honesty in the README is the
    model), the product says so in the UI — Splashtop's 240 fps headline vs its 60 FPS app
    cap is the opposite.
17. **Identity is issued, never typed.** No 9-digit IDs + passwords as a trust root
    (AnyDesk), no 6-digit PINs (CRD), no key files for users to sprawl (SSH), no TOFU walls.
    Device codes + approval + keys the user never handles.
18. **Benchmarks or silence.** Any performance number we publish carries its method, rig,
    versions, and raw data (§6) — or we don't publish it. The field's alternative is on
    record: a 2014 self-run test still cited in 2026.

---

## 6. BENCHMARK PROTOCOL — the head-to-head we will publish

**Why this section exists:** the performance track's central finding is that **no Grade-A
head-to-head comparison exists for this entire market** — the most-cited desktop benchmark
is AnyDesk's self-run 2014 test (AnyDesk 1.0 beta vs TeamViewer 9 on Windows 8.1), Parsec's
"7 ms" is an undefined added-latency claim, Splashtop's 240 fps has never been independently
measured, and file transfer has *zero* controlled multi-product measurements. Publishing one
honest suite is the cheapest superiority in this study. Protocol below merges the
performance track's §7.3 with `eso bench` (BENCHMARKS.md) so our existing tooling extends
rather than restarts.

**Competitors in the suite:** TeamViewer, AnyDesk, RustDesk (OSS server, self-hosted),
Tailscale (direct + DERP-forced) carrying RDP as its desktop answer, SSH/`scp` as the
terminal/file baseline, EsoAgent. Same versions printed on every table; re-run or retract
on major releases.

**Rig (fixed, named, photographed):** one host (named CPU/GPU/RAM; Windows 11, plus a Linux
host pass), one client with a 60 Hz *and* a 144 Hz display (display Hz floors latency quanta
— name it), gigabit wired LAN via one named switch; Wi-Fi 6 client as a separate table.

**Network profiles (netem-shaped, printed with results):**
(a) LAN direct; (b) WAN 25 ms RTT / 100 Mbps; (c) WAN 50 ms / 20 Mbps; (d) WAN 100 ms /
5 Mbps + 1% loss; (e) relay-forced at profile (c) — UDP blocked so every product takes its
relay path. **The path (direct/relayed) is logged for every single run.**

**The measurements (exact):**
1. **Install:** download size (bytes, version, date); install time (stopwatch, fresh VM
   snapshot, default options); **time-to-first-session** (download → controlling a desktop /
   shell, same operator); **clicks-to-first-session** (counted per UI-OVERHAUL.md's
   convention); prerequisites encountered (any external install = a named failure).
2. **Connect time:** click Connect → first frame rendered (desktop) / first prompt (shell),
   median + p95 over ≥30 runs, per network profile.
3. **Latency:** input-to-photon by high-speed camera (input device + client display in one
   frame, direct-cable control condition), ≥100 samples for the office workload, 500 for the
   headline table; report median + p95; **state which quantity** — added pipeline vs
   end-to-end — and the display's Hz. Workloads: scripted office (scroll, window drag,
   typing echo), 60 s 1080p video (same file), fast-motion scene.
4. **Sustained fps + quality + bandwidth together:** client-side fps counter cross-checked
   by camera; bandwidth (Mbps avg/p95, interface counters both ends); image quality as SSIM
   against a local capture on standard frames + an 8-pt text legibility screenshot.
   Bandwidth without a quality lock is not reported (the classic trap).
5. **CPU/RAM:** host agent + client, idle 60 s and in-session per workload, sampled 1 Hz
   with a named tool; server footprint measured same-VM for all servers (relay cost and
   management-plane cost as **separate rows** — the RustDesk 14.5 MiB vs MeshCentral
   284 MiB lesson).
6. **File transfer:** 1 MB / 100 MB / 1 GB, timed, per profile, vs `scp` on the same path;
   resume-after-kill behaviour recorded (kill at 50%, does it resume?).
7. **Tunnel throughput:** iperf3 through each product's tunnel/mesh — single-stream and
   parallel, packet-size matrix (small-packet UDP + large-packet TCP, because rankings
   invert), direct vs relayed as separate rows, loss profiles included (where TCP relays
   fall apart).
8. **Terminal feel (the SSH row):** echo latency distribution for `ssh host true`-class
   round trips and interactive typing echo, LAN + profile (c); reconnect time after an IP
   change (roaming).

**Fairness rules (non-negotiable, printed with the results):** defaults first, then a
matched-quality pass (1080p60 target); hardware-encode on/off as separate matrices with
encoder named per cell (NVENC/QuickSync/AMF/software + codec); ≥30 runs (500 for camera
headlines), median + p95 + min/max; no loopback-only claims for WAN products; no mixing
relayed and direct in one average; per-cell provenance labels (vendor claim / own
measurement / community report); **raw CSVs, scripts, settings exports, and rig photos
published** — the package, not any single number, is what no competitor offers.

---

## 7. Prioritized superiority backlog

Ordered by (lead gained × evidence strength) ÷ effort. Each item names who it beats.

1. **Publish the §6 benchmark suite** — beats *everyone at once*: the field's evidence base
   is a 2014 vendor self-test and unmeasured marketing. Includes timed install/clicks
   (beats MeshCentral's hours, proves the AnyDesk gap honestly) and the `scp` file-transfer
   baseline nobody has ever published. **(M)**
2. **Ship the embedded mesh engine** (already in implementation) — closes the last
   external-install defect; beats MeshCentral (Node.js), RustDesk (two daemons, 6 ports,
   empty-key traps), and every cloud product's enrolment model on the "one installer,
   nothing external" axis. Acceptance: mesh up on a Windows machine with no WireGuard
   installed, incl. a CGNAT path. **(M)**
3. **Hardware H.264 desktop encode** — the only row where we are last. Beats our own JPEG
   ceiling; makes us measurable against Parsec's 10–16 ms [measured] instead of
   incomparable; Splashtop's 240 fps claim has no independent data to defend itself with.
   **(L)**
4. **Phase 3 session chrome + transfer queue UI** — fullscreen/scaling/stats chrome and a
   file queue with progress/cancel/resume: closes the UI-ease gap vs TeamViewer and the
   file-transfer parity notch vs SSH (§4). **(M)**
5. **Installer diet: 124 MB → <60 MB** — AnyDesk's 8.2 MB is the reference users quote;
   we won't match it with a self-contained .NET stack, and we should say so — but halving
   the download halves the first impression gap. **(M)**
6. **End-to-end stream content encryption + signed mesh-record distribution** — extends the
   architecture lead: Tailscale needed Tailnet Lock to answer the compelled-server problem
   and it still doesn't cover DoS/stolen devices; our relay becomes unable to *read*, not
   just unable to forge. **(M)**
7. **Step-up confirmation for sensitive commands + enforce-recording option** — the
   Tailscale check-mode/`enforceRecorder` patterns, absorbed: beats SSH's no-2FA reality
   and Tailscale's Premium paywall for advanced SSH controls in one move. **(S)**
8. **Privileged-broker minimisation audit + published threat model** — converts the
   market's #1 CVE class (Splashtop/Parsec/AnyDesk/Tailscale privesc chains) into a tested
   invariant list (§5 rules 3–6) shipped with the security page. **(S)**
9. **Remote (reverse) forwarding** — completes SSH tunnel parity (§4's last protocol gap).
   **(M)**
10. **`~/.ssh/config` import + SSH-shaped exec surface** — switching-cost killer: SSH's
    defenders keep it from muscle memory; meet them where they are. **(S)**
11. **Fleet-server standby + published relay capacity numbers** — reliability row: our
    answer to Splashtop's outage cadence is "your server, your fix," made credible with a
    standby story and soak data. **(M)**
12. **Remote printing, then session audio** — feature-breadth parity vs TeamViewer/Splashtop;
    RealVNC charges its top tier for less. **(M each)**
13. **Mobile app (Android first)** — every rival has one; MeshCentral's is view-only, CRD's
    iOS app was deprecated to a PWA. A control-capable Android client beats both stories.
    **(L)**
14. **MSI/GPO package + enrolment-by-QR** — scale-deployment parity vs TeamViewer's
    enterprise story, without its Tensor paywall. **(M)**
15. **macOS head** — Core is platform-neutral by design; Parsec can't host on Linux, Tailscale
    can't show a desktop — a three-OS fleet with a desktop is a claim none of the mesh
    rivals can make. **(L)**
16. **TCO comparison page** (10 devices, 3 years, licences + hosting + paywalled features
    priced in) — weaponises §5 rule 14 against TeamViewer/AnyDesk/Tailscale pricing pages.
    **(S)**

---

## 8. What this study did not establish (honest edges)

- No independent latency measurement exists for TeamViewer, AnyDesk (current), RustDesk,
  Splashtop, CRD, RealVNC, or MeshCentral — §6 exists because of this, and until it runs,
  our smoothness claims stay scoped to what BENCHMARKS.md measured.
- DWService/HelpWire lack a sourced profile in this round (§2.11) and any independent
  measurements anywhere (performance track §8).
- Pricing figures are November 2026 snapshots from the cited pages and drift; the TCO page
  (backlog #16) must re-verify each figure at publication.
- Several incident accounts rest partly on vendor statements (labelled `[vendor claim]`
  where used) — notably TeamViewer 2024's "customer data unaffected" and the reported (not
  vendor-confirmed) AnyDesk source-code theft. The study's moves do not depend on the
  contested parts.

*Research corpus: `~/workspace/.comp-study/` (12 track files, ~716 KB, per-claim sources).
EsoAgent state: README.md, FEATURE-MATRIX.md, BENCHMARKS.md, UI-OVERHAUL.md,
MESH-EMBED-SPIKE.md, LAUNCH-REPORT.md in this repo.*
