# Project guidance

Kanka Git Sync is a C# / .NET 10 global tool (`git kanka`) that records a Kanka campaign in a Git repository. Git is the accepted history; `kanka/live` is the last observed campaign state. See the [README](README.md) for orientation and the [GitHub Wiki](https://github.com/Kirtanei/KankaGitSync/wiki) for the full manual.

## Project ruleset

Follow [the project ruleset](docs/project-ruleset-v0.1.md). It is authoritative; the rules below are the agent-facing summary. Use C# / .NET for the application and Vue if a frontend is introduced.

## Tech stack and layout

- .NET 10 SDK, C# with nullable + implicit usings, `TreatWarningsAsErrors` and SonarAnalyzer.CSharp (warnings fail the build — see [Directory.Build.props](Directory.Build.props)).
- Single app project [src/KankaGitSync](src/KankaGitSync) packaged as the `git-kanka` tool; tests in [tests/KankaGitSync.Tests](tests/KankaGitSync.Tests) using xUnit + coverlet.
- Key source files: [CommandLine.cs](src/KankaGitSync/CommandLine.cs) (CLI dispatch), [SyncService.cs](src/KankaGitSync/SyncService.cs) (orchestration), [GitRepository.cs](src/KankaGitSync/GitRepository.cs) (Git plumbing), [KankaAdapter.cs](src/KankaGitSync/KankaAdapter.cs) / [KankaClient.cs](src/KankaGitSync/KankaClient.cs) (remote), [Planner.cs](src/KankaGitSync/Planner.cs), [PushExecutor.cs](src/KankaGitSync/PushExecutor.cs), [OperationLedger.cs](src/KankaGitSync/OperationLedger.cs), [SemanticMerge.cs](src/KankaGitSync/SemanticMerge.cs).
- Read [docs/architecture.md](docs/architecture.md) before changing sync, planning, Git bookkeeping, or adapter behavior.

## Build, test, and verify

Commands and gates are in the [README quick start](README.md#quick-start). After any code change run the release build, analyzer checks, and the full test suite:

```sh
dotnet build -c Release --no-restore
dotnet format --no-restore --verify-no-changes
dotnet test -c Release --no-build -p:CollectCoverage=true -p:CoverletOutputFormat=teamcity -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total
```

Coverage output stays in the console. Keep any temporary artifacts under the ignored `artifacts/` directory so they do not clutter Git.

## Conventions and invariants

- Use descriptive full-word names and keep cognitive complexity at or below 15.
- Never write directly to Kanka outside the synchronization workflow. There are no API DELETE calls and no writes to permissions endpoints.
- Preserve unknown remote fields; send only explicitly managed field patches. Every update is reread immediately before mutation and verified after.
- Treat remote input, YAML, Git paths, and pagination URLs as untrusted.
- Tokens may be stored only in ignored local `.env` files or supplied through the environment. Never commit tokens or output them in diagnostics. See [docs/security-review.md](docs/security-review.md).

## Workflow

- Always commit and push completed changes after the required checks pass, unless the user explicitly instructs otherwise. Do not ask for separate confirmation; report any commit or push failure.
- Treat the GitHub Wiki as the reader-facing manual. Update the relevant Wiki pages and the README in the same change when commands, workflows, configuration, installation, update behaviour, safety limits, architecture, or release behaviour changes. Update command references and diagrams when they are affected, and state explicitly when a change has no documentation impact.
