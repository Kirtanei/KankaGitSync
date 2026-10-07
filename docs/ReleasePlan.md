# Simple, certified 1.0 release plan

## Release contract

- Release `1.0.0` as a Windows-only product: self-contained `win-x64` and `win-arm64` installers.
- Certify only the existing core surface as FULL: managed entity fields, tags, properties, posts, relations, and deliberate tombstone deletion. Keep permissions, binary/media assets, detailed module fields, and unknown modules explicitly READ_ONLY or UNMANAGED.
- Retain the documented editing-pause requirement during push. Do not claim race-free concurrent writes because Kanka has no documented conditional-write guarantee.
- Support the latest stable 1.x release only; publish security fixes promptly and retain the existing private security-reporting route.
- Keep the release deliberately simple: no OAuth, no new Kanka module work, no automatic installer updater, no code-signing purchase, and no multi-platform installer effort.

## Required product and documentation changes

- Replace all v0.1/non-production wording with a precise 1.0 support declaration, including the certified resource table, minimum Git/Windows requirements, administrator-token/visibility requirement, editing-pause requirement, and explicit unsupported surfaces.
- Update the README, Wiki installation/daily-workflow/recovery pages, architecture record, security review, acceptance procedure, and verification record together.
- Change `git kanka update` for installer deployments to report that manual upgrade is required and print the official release page plus checksum and attestation verification commands. Preserve its existing global-tool behavior only if it remains a supported developer path; otherwise remove it from the public 1.0 documentation.
- Add an installer deployment marker beside the executable so updater behavior is deterministic rather than inferred from a path.
- Version the application, installer metadata, documentation, and tag as `1.0.0`.

## Release engineering and supply chain

- Replace the split release process with one tag-triggered Windows release workflow that:
  - runs locked restore, format verification, release build, analyzer checks, full tests, and coverage gate;
  - publishes and installs self-contained x64 and Arm64 artifacts;
  - builds the Inno Setup installers;
  - names assets with version and architecture;
  - generates a single SHA-256 manifest for every release artifact;
  - creates GitHub provenance attestations for each installer and checksum manifest;
  - attaches installers, checksums, verification instructions, and attestations to the GitHub Release.
- Use an unsigned-installer trust model: state prominently that Windows SmartScreen or enterprise policy can block the installer, and provide supported `Get-FileHash` and `gh attestation verify` commands. GitHub attestations provide verifiable build provenance but do not make an unsigned installer trusted by Windows. [GitHub artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations), [Microsoft code-signing guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- Do not pursue EV/OV certificates, Azure signing, Microsoft Store publication, self-signed certificates, or SignPath as 1.0 prerequisites.
- Maintain the existing Windows and Linux CI build/test matrix as engineering coverage, but make Windows x64/Arm64 the only supported runtime/install matrix.

## Certification and release-candidate gate

- Create one `1.0.0-rc.1` from the final candidate commit; run the exact tag workflow and test release artifacts before creating `v1.0.0`.
- Perform the complete disposable-campaign acceptance procedure using a campaign administrator token and a real player account. Record Kanka version/API observations, sanitized request shapes, tool version, Windows version/architecture, Git version, and pass/fail evidence.
- Require live proof of:
  - zero-write initial import and repeated fetch;
  - owner and player UI edits that block push until reviewed;
  - managed create/update/delete round trips for entities, tags, properties, posts, relations, privacy, cross-references, and circular references;
  - unmanaged fields and permissions remaining unchanged;
  - non-overlapping merge, competing-edit conflict, privacy conflict, and explicit deletion acknowledgement;
  - interrupted update and lost-create-response recovery using a disposable-campaign HTTPS proxy that forwards the write then drops the response; verify durable intent, refetch, no blind replay, and manual reconciliation flow.
- Run clean-VM smoke tests for both installer architectures:
  - download from the RC release rather than the build workspace;
  - verify SHA-256 and GitHub attestation before execution;
  - exercise SmartScreen-warning documentation, install, PATH refresh, `git kanka help`, wizard setup with a disposable token, import/fetch, manual upgrade, uninstall, and preservation of world repositories and `.env` files.
- Repeat the release workflow and smoke checks for final `v1.0.0`; promote only if every certification row passes. Any failed preservation, zero-change, recovery, or verification check blocks release.

## Test and acceptance requirements

- Keep the existing unit, HTTP-contract, Git integration, CLI, security, and round-trip suites; add regression coverage for installer-marker detection, manual-update output, release-asset naming/manifest generation, and checksum/attestation instructions.
- Add sanitized live-response fixtures for every certified resource and supported Kanka API shape discovered during certification; replay them in CI to prevent protocol regressions.
- Require release-build, formatting, analyzer, full-test, and console-only coverage gates to pass with at least 80% total line coverage.
- Publish the completed certification matrix and known limitations with the final release. The release notes must explicitly state that Kanka writes affect live data, permissions remain unmanaged, visibility is token-dependent, and publication requires an editing pause. [Kanka API overview](https://app.kanka.io/api-docs/1.0/overview), [Kanka setup and token guidance](https://app.kanka.io/api-docs/1.0/setup)

## Assumptions

- A disposable Kanka campaign, an administrator token, a separate player account, Windows x64 and Arm64 test machines/VMs, and GitHub Release permissions are available to the maintainer.
- GitHub Actions artifact attestations are available because the repository is public; no paid signing or hosting service is required.
- The 1.0 promise is a narrowly certified synchronization core, not complete Kanka API parity.
