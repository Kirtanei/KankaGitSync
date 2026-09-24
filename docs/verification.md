# Verification record

Local verification on Windows, 2026-09-24, with .NET SDK 10.0.401:

| Check | Result |
| --- | --- |
| Locked NuGet restore | Passed |
| Release build, compiler and SonarAnalyzer | Passed; zero warnings/errors |
| Full xUnit suite | 85 passed, zero failed/skipped |
| Console-only coverage | 90.36% lines, 80.71% branches, 95.43% methods |
| 80% line coverage gate | Passed |
| `dotnet format --verify-no-changes` | Passed |
| NuGet vulnerability scan, including transitive dependencies | No known vulnerable packages reported |
| C# duplication scan | 0 duplicated blocks/lines across 16 production files |
| .NET tool package | Built and installed in ignored artifacts/tools |
| CLI help smoke test | Packaged executable starts successfully |

Coverage used `CoverletOutputFormat=teamcity`, which prints statistics without generating report files. Duplication used jscpd 5.3.2 with a five-line/fifty-token minimum and a 3% threshold, console reporter, and bin/obj excluded.

Tests use simulated API responses and real temporary Git repositories. They include HTML/reference/resource round trips, local ID retention after renames, player and owner edits, semantic merging, conflicts, private creation, property/post/relation creation and updates, repeated zero-write pushes, partial failures, concurrent changes, hostile pagination, malformed YAML, and encoded credentials in responses.

VS Code Problems diagnostics and a connected SonarQube server were unavailable. Repository compiler/SonarAnalyzer checks and manual security review were used; no SonarQube server quality-gate result is claimed. Linux CI is configured but was not executed in this local Windows session. No live Kanka campaign was contacted. Follow acceptance.md before production adoption, and account for the concurrency limitation in architecture.md.
