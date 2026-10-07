# Kanka Git Sync

Kanka Git Sync 1.0 records a Kanka campaign in a Git repository while leaving Kanka available to players and the GM. Git is the accepted history; `kanka/live` is the last observed campaign state.

## Supported product

The supported runtime is Windows 10 22H2 or later with Git for Windows 2.44 or later on `PATH`. Download the self-contained `win-x64` or `win-arm64` installer from the [GitHub Releases page](https://github.com/Kirtanei/KankaGitSync/releases). The installer does not bundle Git or require the .NET SDK.

Installers are unsigned. Windows SmartScreen or enterprise policy can block them. Before execution, compare the installer SHA-256 with the release checksum manifest and verify GitHub build provenance:

```powershell
Get-FileHash .\KankaGitSync-<version>-win-<architecture>-Setup.exe -Algorithm SHA256
gh attestation verify .\KankaGitSync-<version>-win-<architecture>-Setup.exe --repo Kirtanei/KankaGitSync
```

GitHub attestations prove build provenance; they do not make an unsigned installer trusted by Windows. Installer deployments upgrade manually by downloading and verifying a new installer. `git kanka update` remains available for the public .NET global-tool installation path.

The latest stable 1.x release is supported. Report suspected vulnerabilities privately through GitHub Security Advisories; never include tokens or campaign content in public issues.

## Certified synchronization core

| Surface | 1.0 support |
| --- | --- |
| Managed entity fields, tags, properties, posts, relations | FULL, subject to release certification |
| Explicit committed tombstones | DELETE only with `--allow-delete`, subject to release certification |
| Permissions, binary/media assets, detailed module fields | READ_ONLY or UNMANAGED; never changed by push |
| Unknown modules and fields | Observed/preserved; never write-managed |

Use a campaign administrator token with visibility of every managed resource. Coordinate an editing pause while publishing: Kanka does not document conditional writes, so the final reread/write race cannot be eliminated. Kanka writes affect live data. Permissions remain unmanaged.

## Daily workflow

```sh
git kanka fetch
git kanka status
git kanka diff
git kanka pull

git kanka validate
git add world
git commit -m "Describe the new location"
git kanka plan
git kanka push
```

To delete managed content, create and commit a tombstone with `git kanka delete <local-id>`, inspect `git kanka plan`, then use `git kanka push --allow-delete`. Removing a local file never deletes Kanka content.

## Global-tool installation

The global-tool route remains supported for users who need it and requires .NET 10 SDK plus Git on `PATH`:

```sh
dotnet tool install --global KankaGitSync
git kanka help
git kanka update
```

For development from a checkout, restore with `dotnet restore --locked-mode`, build and test in Release, pack to `artifacts/packages`, then install using `--add-source ./artifacts/packages`.

The full manual is in the [GitHub Wiki](https://github.com/Kirtanei/KankaGitSync/wiki): [installation](https://github.com/Kirtanei/KankaGitSync/wiki/Installation), [daily workflow](https://github.com/Kirtanei/KankaGitSync/wiki/Daily-Workflow), [command reference](https://github.com/Kirtanei/KankaGitSync/wiki/Command-Reference), [architecture](https://github.com/Kirtanei/KankaGitSync/wiki/Architecture-Overview), and [safety/recovery](https://github.com/Kirtanei/KankaGitSync/wiki/Troubleshooting-and-Recovery).

Repository-controlled records: [architecture and limitations](docs/architecture.md), [security review](docs/security-review.md), [verification](docs/verification.md), [release certification](docs/release-certification.md), [project ruleset](docs/project-ruleset-v0.1.md), and [live acceptance checks](docs/acceptance.md).
