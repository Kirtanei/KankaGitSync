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

Provide `KANKA_TOKEN` through your shell environment or secrets manager. Never put its value in a command argument, config file, or Git. The tool does not load `.env` files.

Create a token in Kanka's **Profile → API** settings, then use the instructions for your shell below. Paste the token at the hidden prompt and press Enter. Its value will not be displayed or saved in your shell's command history.

**Git Bash (Windows), Bash (Linux/macOS):**

```bash
read -rsp 'Kanka API token: ' KANKA_TOKEN
printf '\n'
export KANKA_TOKEN
```

**PowerShell (Windows PowerShell 5.1 or PowerShell 7):**

```powershell
$secureToken = Read-Host 'Kanka API token' -AsSecureString
try {
    $env:KANKA_TOKEN = [System.Net.NetworkCredential]::new('', $secureToken).Password
}
finally {
    $secureToken.Dispose()
    Remove-Variable secureToken
}
```

Run the following commands in that same terminal. The environment variable lasts only for the current shell session and its child processes; repeat the prompt when you open another terminal. `--campaign 123` above takes your numeric campaign ID, never the API token.

```sh
git kanka import
git kanka validate
git kanka plan
```

When finished, clear the token with `unset KANKA_TOKEN` in Bash or `Remove-Item Env:KANKA_TOKEN` in PowerShell, or close the terminal. If you accidentally paste a token into a command, chat, or Git, revoke it in Kanka and create a replacement.

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
