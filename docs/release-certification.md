# 1.0 release certification

Do not create `v1.0.0` until every item below passes. Record sanitized evidence only; never commit tokens, campaign content, or raw HTTP responses containing credentials.

## Release candidate

1. Set the final candidate commit version to `1.0.0` and create `v1.0.0-rc.1`.
2. Confirm the tag workflow passes locked restore, formatting, Release build/analyzers, full coverage-gated tests, package creation, both installers, checksums, attestations, and release attachment.
3. Download the RC installers from GitHub Releases, not the workspace. Verify the checksum manifest and GitHub attestation before each test.

## Campaign certification

Run `docs/acceptance.md` against a disposable campaign with an administrator token and a separate player account. Capture tool version, Windows version/architecture, Git version, Kanka/API observations, sanitized request shapes, and pass/fail evidence.

Require zero-write initial import/repeated fetch; owner and player UI edits that block push; managed create/update/delete round trips for entities, tags, properties, posts, relations, privacy, and circular references; preservation of unmanaged fields and permissions; semantic merge and conflict cases; and explicit deletion acknowledgement.

Use an HTTPS proxy to forward one write then discard its response. Verify durable intent, refetch-based recovery, no blind replay, and the documented manual reconciliation flow for both update and lost-create responses.

## Clean-VM certification

On clean Windows 10 22H2-or-later x64 and Arm64 VMs, verify the downloaded RC checksum and attestation; document the SmartScreen/policy warning; install; refresh PATH; run `git kanka help`; use the wizard with a disposable token; import and fetch; exercise manual upgrade; uninstall; and confirm world repositories and `.env` files remain.

## Final release

Repeat the tag workflow and both smoke-test matrices for `v1.0.0`. Publish the completed matrix and known limitations in the release notes. Any preservation, zero-change, recovery, verification, or VM failure blocks the release.
