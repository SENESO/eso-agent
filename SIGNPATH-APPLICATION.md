# SignPath Foundation — Application Data Sheet (EsoAgent)

**Status: DECLINED 2026-11-19 (not final).** Submitted by the owner himself
2026-11-18 21:04. SignPath replied 2026-11-19 (Phillip Deng,
oss-support@signpath.org): "we're unable to approve your application at this
time." Reason: insufficient public visibility signals (stars/forks/contributors,
external coverage, independent discussions, sustained activity). Reapply when
visibility grows; paid subscription is the immediate alternative (owner
decision/hands). No SignPath account has been created; releases stay unsigned
meanwhile.

**Verification note:** Eligibility conditions below were re-verified firsthand
on 2026-11-18 against the official source, <https://signpath.org/terms.html>
("SignPath Foundation conditions for Open Source projects"), and the apply
page <https://signpath.org/apply>. The application form itself is a
JavaScript-rendered HubSpot embed whose field list cannot be read by a text
fetch; the core fields in §2 are the ones consistently reported for this form
(repository URL, download page URL, project description, plus standard
contact fields). Confirm exact labels at submission time.

---

## 1. What SignPath Foundation offers (verified)

- Free code signing for qualifying open-source projects, via a free
  SignPath.io subscription.
- The certificate is issued by Sectigo **to "SignPath Foundation"** — the
  publisher name users see in Windows SmartScreen / installer dialogs is
  **"SignPath Foundation"**, not the project or maintainer name. (Known and
  accepted trade-off, owner's decision 2026-11-18.)
- The private key never leaves SignPath's HSM; signing happens inside the
  build pipeline (GitHub Actions connector), and **every release's signing
  request needs manual approval** by a project Approver.
- No personal identity verification of the maintainer is performed — the
  Foundation verifies the project/repository and vouches with its own name.
- The Foundation may pause/terminate the subscription or revoke the
  certificate (even retroactively) if its Code of Conduct is violated.

## 2. Application form — answers ready to paste

| Field (expected) | Value |
|---|---|
| Project name | EsoAgent |
| Repository URL | https://github.com/SENESO/eso-agent |
| Project website / home page | https://seneso.github.io/eso-agent/ |
| Download page URL | https://github.com/SENESO/eso-agent/releases (latest: Release v1.2.0, https://github.com/SENESO/eso-agent/releases/tag/v1.2.0) |
| Licence | MIT (OSI-approved), no commercial dual-licensing |
| Licence file | https://github.com/SENESO/eso-agent/blob/main/LICENSE |
| Code signing policy | https://github.com/SENESO/eso-agent/blob/main/CODE_SIGNING.md (also linked from the project home page and README) |
| Maintainer / applicant name | Eslam Ashraf Antar |
| GitHub account | SENESO — https://github.com/SENESO |
| Team size | 1 (sole maintainer: author, reviewer and approver are the same person) |
| Build system | GitHub Actions, GitHub-hosted runners, workflow `.github/workflows/release.yml`; the SignPath submission step is already present and activates once `SIGNPATH_ORG_ID` / `SIGNPATH_PROJECT_SLUG` variables and the `SIGNPATH_API_TOKEN` secret are set |
| Artefacts to be signed | Windows installer (`EsoAgent-Setup-1.2.0.exe`, Inno Setup) and the Windows binaries it installs |
| First release in the form to be signed | v1.2.0, published 2026-11-18 (9 assets incl. the Windows installer and a signed `manifest.json`) — currently unsigned, as the policy page states |

**Project description (draft, for the form's description field):**

> EsoAgent is a free, open-source (MIT) remote administration platform for
> people managing their own computers: a cross-platform agent (Windows and
> Linux), a fleet server, a desktop app, a browser control panel and a CLI.
> It provides remote shell, file transfer, software and service management,
> remote desktop, and a private WireGuard mesh — between machines the user
> owns, with no third-party servers and no telemetry. Every operator action
> is cryptographically signed and verified on the device itself; the owner
> alone holds the kill switch, the operator-key revocation and the password
> controls. Releases are built exclusively by GitHub Actions from this
> public repository. Single maintainer.

## 3. Eligibility self-check against the official conditions

Verified against signpath.org/terms.html on 2026-11-18:

| Condition | EsoAgent status |
|---|---|
| No malware / no PUPs | ✅ Legitimate owner-controlled administration tool (see framing note below) |
| OSI-approved licence, no dual-licensing, all components | ✅ MIT throughout; dependencies are MIT-licensed (e.g. Avalonia) |
| No proprietary components | ✅ All components open source |
| Actively maintained | ✅ v1.0 → v1.2.0 shipped 2026-11-18 |
| Already released in the form to be signed | ✅ Release v1.2.0 incl. the Windows installer |
| Functionality documented on download page | ✅ README + project site describe functionality; Releases page is the download page |
| Sign own projects / own binaries only | ✅ Own repository, own CI builds |
| No hacking tools (no vuln-scanning/exploit features) | ✅ EsoAgent has no vulnerability-scanning or exploit features; it is an administration tool for the owner's own machines, gated by owner-set passwords, signed commands, an audit log and a kill switch |
| Respect user privacy | ✅ No telemetry; agent reports only to infrastructure its owner configures. Privacy statement is in CODE_SIGNING.md, using the wording the Foundation suggests |
| Announce system changes / provide uninstallation | ✅ Standard Inno Setup installer with uninstaller; Linux packages likewise |
| Team roles (Authors / Reviewers / Approvers) | ✅ Defined in CODE_SIGNING.md roles section; sole maintainer fills all three |
| Code signing policy on the home page | ✅ CODE_SIGNING.md in repo, linked from README and from the project site. ⚠️ After approval, add the Foundation's exact attribution line — "Free code signing provided by SignPath.io, certificate by SignPath Foundation" — plus the team-role list, as the terms require for certificate holders |
| Artefact metadata (product name/version enforced) | ✅ Single version source (`Directory.Build.props` → 1.2.0) stamps all artefacts; the SignPath artifact configuration itself is set during onboarding |
| MFA for all team members (GitHub **and** SignPath) | ⚠️ **Owner-only — see §4** |

**Honest risk flag:** the terms state that for executable programs the
Foundation requires "a certain verifiable reputation" and that acceptance is
entirely its discretion, with no appeal. EsoAgent is brand-new (first release
2026-11-18), so a rejection or a request to build more history first is
possible. The application is still worth filing now — that was the owner's
decision — but expectations should be set accordingly.

## 4. Owner-only items (cannot be prepared by the assistant)

1. **GitHub MFA:** confirm that two-factor authentication is enabled on the
   SENESO GitHub account. The terms require MFA for **every** team member,
   for both GitHub and SignPath access. (Account security setting — only the
   owner can check/change it.)
2. **Contact details for the form:** the email address the application
   should use (suggestion: ea8199468@gmail.com, the address on his CV/site)
   and confirmation that the applicant name should read "Eslam Ashraf
   Antar". Country: Egypt.
3. **SignPath account:** if the Foundation approves the application, a
   SignPath.io account/organization is created under the owner's identity —
   he creates it / accepts the invite himself, and enables MFA on it.
4. **Final submission:** reviewing this sheet and pressing submit on
   https://signpath.org/apply is the owner's act (this sheet is preparation
   only, per his instruction).
5. **After approval (owner's hands on secrets):** create the SignPath API
   token inside his SignPath account and store it as the GitHub secret
   `SIGNPATH_API_TOKEN`, plus the repository variables `SIGNPATH_ORG_ID`
   and `SIGNPATH_PROJECT_SLUG`. The release workflow's signing step is
   already wired for exactly these names and stays inert until they exist.
6. **Per-release approvals:** once live, every release's signing request
   must be manually approved in SignPath by an Approver — that will be the
   owner (sole maintainer).

## 5. Exact submission steps (when the owner gives the word)

1. Owner confirms GitHub MFA is on for SENESO (§4.1).
2. Open https://signpath.org/apply ("Apply for a free SignPath.io
   subscription") and fill the form with the values in §2.
3. Submit. The Foundation reviews the project against the conditions in §3
   (secondary sources report processing from a few days to a few weeks;
   the Foundation itself publishes no SLA).
4. On approval: owner creates/accepts the SignPath.io OSS organization,
   enables MFA, and creates a project + "release signing" policy for
   EsoAgent in SignPath.
5. Owner adds `SIGNPATH_API_TOKEN` (secret) and `SIGNPATH_ORG_ID` /
   `SIGNPATH_PROJECT_SLUG` (variables) to the GitHub repo.
6. Add the Foundation's attribution line + team roles to the code-signing
   policy on the home page (§3, policy row).
7. Trigger the release workflow for the next version; approve the signing
   request in SignPath; the installer ships signed with publisher
   "SignPath Foundation".

---

*Prepared 2026-11-18. Sources: signpath.org/terms.html and signpath.org
(read firsthand 2026-11-18); project record in this repository (README,
LICENSE, CODE_SIGNING.md, .github/workflows/release.yml) and LAUNCH-REPORT.md.*
