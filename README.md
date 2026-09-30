# Kanka Git Sync

C# / .NET 10 command-line synchronization between a worldbuilding Git repository and a Kanka campaign. Kanka remains an editing surface for players and the GM. Git records accepted history. No frontend is needed for this release; a future frontend will use Vue.

The [project ruleset](docs/project-ruleset-v0.1.md) is the design contract. This implementation covers the non-destructive v0.1 workflow. **It has not been validated against a live campaign and is not certified production-ready.** Start with a disposable campaign and complete the [acceptance procedure](docs/acceptance.md).

## Build and install

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

### Upgrade and recover a zero-resource import

Version 0.1.1 fixes category detection for Kanka's current entity responses. Version 0.1.0 could preserve every entity as an unmanaged snapshot while reporting a successful import with zero editable resources.

Version 0.1.2 also handles boolean checkbox values in API responses and includes a code location in sanitized failure messages. Earlier versions could fail with `InvalidOperationException` when reading a checkbox as text.

From this application's source directory, rebuild and update your installed tool:

```sh
dotnet build -c Release
dotnet pack src/KankaGitSync -c Release --no-build -o artifacts/packages
dotnet tool update --global --add-source ./artifacts/packages --version 0.1.3 KankaGitSync
```

Then return to your world repository, create your persistent `.env` as described below, and recover through the normal fetch/merge workflow:

```sh
git kanka fetch && git kanka pull
git diff --cached --stat
git kanka validate
git commit -m "Import campaign resources with corrected category detection"
git kanka plan
```

Keep the existing repository and configuration; `import` is only for the first import. Fetch can take a long time for large campaigns because it reads entity details and attachments under the configured API rate limit. Unsupported modules remain in `.kanka/remote` and produce a warning.

Stop if any command fails. A failed fetch leaves the previous `kanka/live` snapshot in place, so subsequent validation can still report the old zero-resource import. The `&&` above prevents pulling that stale snapshot after a fetch failure in Bash or PowerShell 7.

## First import

Use a **separate repository for your world**, outside this application's source tree. The API token must belong to a campaign administrator who can see all managed content. Restricted visibility can otherwise look like deletion. Git stores private campaign material too; choose repository access accordingly.

```sh
mkdir my-world
cd my-world
git init -b main
git kanka init --campaign 123
git add .gitignore .kanka
git commit -m "Configure campaign synchronization"
```

Create `.env` in your **world repository root** and enter your Kanka API token once:

```dotenv
KANKA_API_TOKEN=your-token-here
```

Version 0.1.3 loads this file automatically, including when you run commands from subfolders or open a new terminal. `init` adds `.env` to `.gitignore`; keep it ignored and never commit the token. The application's `.env.example` contains a placeholder you can copy. Existing `.env` files are never overwritten.

Both `KANKA_API_TOKEN` (as used by KankaPlugin) and `KANKA_TOKEN` are supported. Nonblank shell environment values take precedence over file values; within either source, `KANKA_TOKEN` wins. Blank values are treated as absent. Remove an old shell override if you want to use the file value.

The file accepts plain or single/double-quoted single-line values, comments beginning with `#`, whitespace, and optional `export`. Quote values containing `#`. Contents are literal: no variable expansion, escape processing, or command execution occurs. Unrelated settings are ignored; repeated token settings use the last value. Malformed token entries fail without printing their contents. Offline commands do not read credentials.

Create the token in Kanka's **Profile > API** settings. Keep campaign IDs in `.kanka/config.yml`; `--campaign 123` takes your numeric campaign ID. Never put a token in command arguments, Git, or diagnostics.

```sh
git kanka import
git kanka validate
git kanka plan
```

Import fetches the campaign onto `kanka/live`, validates it, and fast-forwards `main` to that exact state. The plan must contain zero mutations. A second import is refused; subsequent reads use `fetch`.

## Normal workflow

```sh
git kanka fetch
git kanka status
git kanka diff
git kanka pull
git diff --cached
git commit -m "Review Kanka contributions"
```

`fetch` changes only the generated branch, preserving staged and unstaged authoring changes. `pull` uses the last fetched branch and stages a merge for review without committing it. Conflicts remain in Git. Different YAML fields can merge semantically; competing prose edits use Git's merge behavior. Simultaneous privacy edits and delete/edit conflicts require an explicit decision.

Edit world files, then validate and commit them before publishing:

```sh
git kanka validate
git add world
git commit -m "Update world history"
git kanka plan
git kanka push
```

Both `plan` and `push` fetch first and reject unintegrated remote changes. `plan` issues no API writes. `push` prints its freshly computed plan, applies minimal patches, refetches, and verifies the result. The accepted input is the committed `main` branch. An uncommitted working tree blocks planning and publication.

New resources require `publish: true`. Publication and any privacy change require review of the plan and `git kanka push --approve-privacy`. Every new entity starts private; content and cross-references are applied only after all entity IDs have been recorded. Publication never reads `gm/`, `drafts/`, or `research/`.

To reject remote contributions deliberately, inspect the diff and make a Git merge commit that records your chosen resolution. For example, `git merge --no-commit --no-ff kanka/live`, edit the affected files, stage, and commit. There is no force-push option. Never edit `kanka/live` directly.

## World format

```text
world/characters/prince-maximilian/
  index.md
  properties.yml
  relations.yml
  posts/alice-journal.md
.kanka/
  config.yml
  users.yml
  ids.yml
  remote/                    # preserved API data and provenance
gm/                          # never published
```

An entry's `index.md`:

```markdown
---
id: prince-maximilian
name: Prince Maximilian
category: character
type: Prince of Cumbria
publish: false
visibility:
  private: true
tags: []
fields:
  title: Prince
  age: "39"
  sex: ""
  pronouns: he/him
---

Visit [[tarant|the city of Tarant]].
```

Local IDs are immutable, globally unique, lowercase words separated by hyphens (at most 100 characters). Import derives a readable ID once, resolves collisions, and keeps the mapping across renames and deletions. Numeric Kanka IDs live only in `.kanka/`. An absent `publish` flag defaults to false.

Properties and relations use mappings keyed by their immutable local IDs:

```yaml
# properties.yml
maximilian-population:
  id: maximilian-population
  name: Population
  type: number
  value: "850000"
  private: false
  publish: true
```

```yaml
# relations.yml
maximilian-tarant:
  id: maximilian-tarant
  target: tarant
  relation: lives in
  attitude: 40
  visibility: all
  publish: true
```

Posts use Markdown with `id`, `name`, `visibility`, and `publish` front matter. Post and relation visibility is one of `all`, `self`, `admin`, `self-admin`, `members`. Property types are `text`, `paragraph`, `checkbox`, `section`, `random`, `number`, `choice`. Quote scalar text that resembles a number or boolean. YAML aliases, anchors, duplicate keys, and arbitrary object tags are not supported.

Unsupported HTML is retained raw when conversion would lose information. Recognized `[entity:ID]` mentions become local references; unsupported mention syntax is retained. Unmanaged fields, permissions, and related data remain in the adapter snapshots and are never included in lore update patches. Common entity name/type/body/privacy/tags and character title/age/sex/pronouns are editable; other type-specific fields remain observable in `.kanka/remote/`.

## Authorship and recovery

`.kanka/users.yml` maps Kanka user IDs to roles. Configure owners explicitly on `main`:

```yaml
"12345":
  name: Jesper
  role: owner
```

Campaign members discovered by fetch default to players. Unknown users remain unknown. Polling cannot establish every original author; status labels the last observed editor as possible attribution. Generated commits use `Kanka Sync <kanka-sync@localhost>` and record polling ambiguity in trailers. No player email addresses are invented.

The shared Git directory holds `kanka-ledger.jsonl` and a process lock. The ledger flushes write intentions before HTTP calls and records returned IDs and hashes after success. Interrupted writes are never automatically replayed. After failure, inspect `git kanka doctor`, fetch, and review the actual campaign. If an interrupted creation succeeded but its response was lost, reconcile the existing remote object with your intended local object before retrying; do not leave a duplicate local creation pending. Only after this review, `git kanka doctor --acknowledge-recovery` acknowledges unresolved outcomes. It does not bypass the Git integration check.

`git kanka delete <id>` records a tombstone for review. Removing a file never sends a deletion. **Deletion execution and permission management are disabled in v0.1**, as prescribed by the staged development scope.

See [architecture and limitations](docs/architecture.md), [security review](docs/security-review.md), and [live acceptance checks](docs/acceptance.md).
