# Code Signing Policy

EsoAgent is free and open-source software (MIT). This page is the
project's published code-signing policy.

## How releases are made

- Releases are built **only** by GitHub Actions from the source in this
  repository, on GitHub-hosted runners, from a version tag (`vX.Y.Z`).
  The workflow is `.github/workflows/release.yml` — anyone can read
  exactly what it does.
- No release binary is built on a maintainer's personal machine.

## Installer signing (SignPath Foundation)

- Windows installers and binaries are signed via
  [SignPath Foundation](https://signpath.org/) (free code signing for
  open-source projects), once the project is approved.
- Until approval, releases ship **unsigned** and are labelled as such.
  A signature is never faked, borrowed, or self-signed and presented as
  verified.

## Update integrity (independent of installer signing)

EsoAgent does not rely on installer signatures for update safety:

- Every release publishes a `manifest.json` listing each artefact's
  SHA-256, signed with the project's **release key** (ECDSA P-256).
- The corresponding public key is **pinned in the product**
  (`ReleaseManifestSigning.PinnedPublicKeySpki`). Devices verify the
  manifest signature and every artefact hash before staging an update,
  and refuse same-version and downgrade packages.
- Updates apply **in place** through the agent's signed self-update
  machinery with rollback; the fleet server cannot forge operator
  commands (zero-trust signed execution), and it cannot forge release
  manifests either — it does not hold the release key.

## Roles

- Release builds: GitHub Actions (this repository).
- Release key custody: project maintainer, held as a CI secret
  (`ESO_RELEASE_PRIVATE_KEY`) and an offline backup; never on devices,
  never in the repository.
- Signing submissions: SignPath Foundation, per their OSS terms, after
  approval.

## Privacy

EsoAgent collects no telemetry about its users. The agent reports only
to infrastructure its owner configures (their own fleet server). See
README.md for the trust model.
