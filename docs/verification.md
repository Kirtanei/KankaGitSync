# Verification record

## 1.0 release preparation

Automated RC.5 certification on 2026-10-10: `v1.0.0-rc.5` (`12b349b`) completed the release workflow successfully. Downloaded Windows x64 and Arm64 installers matched the published SHA-256 manifest, and GitHub CLI attestation verification passed for both installers. The disposable campaign completed lost-response recovery for both update and create: the proxy recorded the accepted origin response, the client did not replay the operation, `doctor --acknowledge-recovery` followed a full refetch and explicit Git reconciliation, and the resulting plan was zero operations. The two-clone matrix confirmed a publication block before concurrent writer mutation, a clean non-overlapping structured/prose merge, an unresolved competing-prose Git conflict, and privacy publication review. All temporary `release-cert-*` remote fixtures were removed through committed tombstones with `--allow-delete`; mappings remain in Git history and the final plan reported zero API operations. No credentials, raw response bodies, or campaign prose are recorded in this document. Final local gates passed: locked restore, Release build, formatting verification, coverage-gated tests, and package generation.

The repository now defines the `1.0.0` Windows release workflow, versioned x64/Arm64 installer assets, SHA-256 manifest, GitHub provenance attestations, installer deployment marker, and manual-update guidance. The automated RC disposable-campaign and lost-response gates are complete; clean-VM and separate-player/UI evidence remains required before `v1.0.0` can be created.

RC.4 live disposable-campaign check on 2026-10-07: the installed Windows x64 client completed a full fetch of eight resources; detected unintegrated external campaign edits and blocked publication; merged the authorized disposable state; produced a zero-operation plan; published one managed character-body update; recorded `refs/kanka/published` and `refs/kanka/verified`; and produced a second zero-operation plan. No credentials or raw responses were recorded. This is partial evidence only; the player UI, lost-response, delete, privacy/conflict, and Arm64 clean-VM gates remain outstanding.

Player-edit and prose-conflict check on 2026-10-07: a player UI edit to the managed character body imported to `kanka/live` with player attribution; a push refetched and blocked without writing; integration against the concurrent local body update created an unresolved Git prose conflict; the authorized disposable resolution retained the remote player content; and a subsequent full plan produced zero API operations. The disposable-world history was pushed to its test repository.

Partial live acceptance on Windows, 2026-10-07, against disposable Kanka campaign `426901` using the documented v1.0 API. The campaign contains only acceptance fixtures; no credentials are recorded here. Release build and formatting verification passed, and all 152 tests passed with console-only coverage of 84.48% lines, 74.01% branches, and 88.92% methods. The following live behaviors were exercised and recovered into Git history:

| Check | Result |
| --- | --- |
| Initial import and repeated fresh import | Passed; zero planned writes after normalized round trip |
| Entity create and completion | Passed for character, location, organisation, private note, and tag fixtures |
| Properties, public/admin posts, relations, tags, cross-references, and private content | Created and re-imported successfully |
| Kanka HTML sanitization | Detected and reconciled; unsupported `data-acceptance` attribute was removed by Kanka and did not silently pass verification |
| Minimal managed-field update | Passed; a body-only edit planned and published as `UPDATE acceptance-character [entry]`, then received verified/published refs |
| Missing local file | Passed; removing and committing an admin-post file planned zero remote deletes |
| Explicit tombstone deletion | Passed; `git kanka delete` created one DELETE plan, a normal push refused it, and `--allow-delete` deleted the disposable post and retained its ID mapping |
| Post-operation verification and durable ledger | Passed for the verified update and delete runs |

This is not 1.0 certification. It does not cover every documented campaign-content module, binary assets, player-editable content exercised through the Kanka UI, interrupted/lost-response recovery against the live API, Linux validation, installer signing/VM smoke tests, or public release-artifact verification. Kanka does not document conditional writes; an editing pause remains required during publication.

Version 0.1.4 verified locally on Windows, 2026-09-30: locked restore, Release build with compiler/SonarAnalyzer (zero warnings/errors), formatting verification, and all 135 tests passed. Console-only coverage was 90.73% lines, 82.53% branches, and 95.73% methods. Tests cover `init-env` from subfolders before campaign configuration, existing-file preservation, Git ignore rules, refusal of tracked credentials, literal token round trips, invalid input, cancellation, and diagnostic secrecy. The packaged 0.1.4 tool was exercised in a real terminal with a fake token: hidden input emitted no token text, the saved value matched, and Git ignored `.env`. Smoke artifacts remain under ignored `artifacts/`. No live Kanka API calls were made.

Version 0.1.3 verified locally on Windows, 2026-09-30: locked restore, Release build with compiler/SonarAnalyzer (zero warnings/errors), formatting verification, and all 122 tests passed. Console-only coverage was 91.30% lines, 83.33% branches, and 96.60% methods, passing the 80% line gate. The 0.1.3 tool package was built and installed under ignored `artifacts/tools`; its help command passed a smoke test. New tests cover persistent token parsing, source/name precedence, blank and malformed credentials, root resolution from subfolders, offline independence, ignore rules, preservation of existing files, and removal of both token variables from Git subprocesses. Temporary test repositories now live under ignored `artifacts/tests`. No live Kanka API calls were made.

Local verification on Windows, 2026-09-24, with .NET SDK 10.0.401:

| Check | Result |
| --- | --- |
| Locked NuGet restore | Passed |
| Release build, compiler and SonarAnalyzer | Passed; zero warnings/errors |
| Full xUnit suite | 100 passed, zero failed/skipped |
| Console-only coverage | 90.61% lines, 81.62% branches, 96% methods |
| 80% line coverage gate | Passed |
| `dotnet format --verify-no-changes` | Passed |
| NuGet vulnerability scan, including transitive dependencies | No known vulnerable packages reported |
| C# duplication scan | 0 duplicated blocks/lines across 16 production files |
| .NET tool package | Built and installed in ignored artifacts/tools |
| CLI help smoke test | Packaged executable starts successfully |

Coverage used `CoverletOutputFormat=teamcity`, which prints statistics without generating report files. Duplication used jscpd 5.3.2 with a five-line/fifty-token minimum and a 3% threshold, console reporter, and bin/obj excluded.

Tests use simulated API responses and real temporary Git repositories. They include HTML/reference/resource round trips, local ID retention after renames, player and owner edits, semantic merging, conflicts, private creation, property/post/relation creation and updates, repeated zero-write pushes, partial failures, concurrent changes, hostile pagination, malformed YAML, and encoded credentials in responses.

Version 0.1.1 additionally covers current and legacy entity module fields, freeform entity types, explicit unsupported modules, missing module identity, and recovery from a previously unmanaged-only import. The default simulated campaign now uses the current `module.code` and `type` response shape. Existing local campaign snapshots were inspected read-only to diagnose the mismatch; the updated importer has not yet completed a live campaign fetch.

VS Code Problems diagnostics and a connected SonarQube server were unavailable. Repository compiler/SonarAnalyzer checks and manual security review were used; no SonarQube server quality-gate result is claimed. Linux CI is configured but was not executed in this local Windows session. No live Kanka campaign was contacted. Follow acceptance.md before production adoption, and account for the concurrency limitation in architecture.md.

Version 0.1.2 adds scalar property import tests, boolean checkbox round trips through world files, zero-change planning, checkbox edits, and safe rejection of structured values. CLI diagnostics now include only compiled application type/method names; a regression test verifies that invalid input content is not exposed. Manual security review found no new credential or campaign-content disclosure in these diagnostics. Boolean checkbox responses reproduce an import failure in the prior implementation, but the precise cause of the reported live campaign failure remains unconfirmed until a successful fetch or a more specific diagnostic is available.
