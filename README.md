# Kanka Git Sync

Kanka Git Sync records a Kanka campaign in a Git repository while leaving Kanka available to players and the GM. Git is the accepted history; `kanka/live` is the last observed campaign state. **v0.1 is not certified for a production campaign.** Use a disposable campaign and complete the [acceptance procedure](docs/acceptance.md) first.

The full manual is in the [GitHub Wiki](https://github.com/Kirtanei/KankaGitSync/wiki): [installation](https://github.com/Kirtanei/KankaGitSync/wiki/Installation), [daily workflow](https://github.com/Kirtanei/KankaGitSync/wiki/Daily-Workflow), [command reference](https://github.com/Kirtanei/KankaGitSync/wiki/Command-Reference), [architecture](https://github.com/Kirtanei/KankaGitSync/wiki/Architecture-Overview), and [safety/recovery](https://github.com/Kirtanei/KankaGitSync/wiki/Troubleshooting-and-Recovery).

> **AI transparency:** This project and its documentation are developed with AI assistance. Human review remains required for code, configuration, operational decisions, and campaign changes. Verify operational claims against the current release and `git kanka help`.

## Quick start

Prerequisites: .NET 10 SDK and Git on PATH.

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build -p:CollectCoverage=true -p:CoverletOutputFormat=teamcity -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total
dotnet pack src/KankaGitSync -c Release --no-build -o artifacts/packages
dotnet tool install --global --add-source ./artifacts/packages KankaGitSync
git kanka help
```

Coverage statistics go to the console. Compiler and SonarAnalyzer warnings fail the build. `dotnet format --no-restore --verify-no-changes` checks formatting. Package dependencies are locked. The CI workflow checks Windows and Linux.

Use `git kanka help` or `git-kanka --help`; Git intercepts `git kanka --help` as a request for an installed manual page.

Update an installed tool from the latest stable public GitHub Release:

```sh
git kanka update
```

No GitHub token is required to use `update`. Then use this normal workflow from a world repository:

```sh
git kanka fetch
git kanka status
git kanka diff
git kanka pull
```

To delete managed Kanka content, create and commit a tombstone with `git kanka delete <local-id>`, inspect `git kanka plan`, and use `git kanka push --allow-delete`. Removing a local file never deletes Kanka content. Coordinate an editing pause while publishing: Kanka does not document a conditional-write API, so a final read/write race remains possible.

## Support and security

Kanka Git Sync is MIT licensed and maintained on a best-effort basis. It supports .NET 10 and Git on Windows and Linux. Report suspected vulnerabilities privately through GitHub Security Advisories; do not include tokens or campaign content in public issues. The current supported surface is documented in the architecture record; do not treat untested Kanka resource types as editable.

The Wiki is the complete manual. Repository-controlled technical records remain available: [architecture and limitations](docs/architecture.md), [security review](docs/security-review.md), [verification](docs/verification.md), [project ruleset](docs/project-ruleset-v0.1.md), and [live acceptance checks](docs/acceptance.md).
