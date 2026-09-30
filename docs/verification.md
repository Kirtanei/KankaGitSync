# Verification record

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
