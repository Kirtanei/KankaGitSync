[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorldPath,
    [string] $ToolPath = (Join-Path $env:LOCALAPPDATA 'Programs\KankaGitSync\git-kanka.exe'),
    [switch] $RunLive,
    [switch] $KeepFixtures,
    [switch] $SkipQualityGates,
    [string] $RunId,
    [switch] $ResumeCleanup
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runId = if ([string]::IsNullOrWhiteSpace($RunId)) { 'release-cert-' + (Get-Date -Format 'yyyyMMddHHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)) } else { $RunId }
$evidence = Join-Path $repositoryRoot "artifacts\release-certification\$runId"
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$fixtureIds = @("$runId-a", "$runId-b", "$runId-tag")
$manifestPath = Join-Path $evidence 'manifest.json'
$certificateThumbprint = $null
$proxyProcess = $null

function Write-Evidence([string] $Name, [string] $Text) {
    $safe = $Text -replace '(?im)(authorization:\s*bearer\s+)[^\s]+', '$1[redacted]' -replace '(?im)(KANKA_(?:API_)?TOKEN=)[^\s]+', '$1[redacted]'
    Set-Content -LiteralPath (Join-Path $evidence $Name) -Value $safe -NoNewline
}

function Invoke-Checked([string] $Name, [scriptblock] $Action) {
    try { $output = & $Action 2>&1 | Out-String }
    catch {
        $output = $_ | Out-String
        Write-Evidence "$Name.txt" $output
        throw
    }
    Write-Evidence "$Name.txt" $output
    if ($LASTEXITCODE -ne 0) { throw "$Name failed. See sanitized evidence." }
    return $output
}

function Require-NoKankaProcess {
    $running = @(Get-Process git-kanka -ErrorAction SilentlyContinue)
    if ($running.Count -ne 0) { throw "A prior git-kanka process is still running ($($running.Id -join ', ')). Wait for it before retrying." }
}

function Wait-KankaExit([int] $RootProcessId) {
    while ($true) {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $RootProcessId" -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty ProcessId)
        $root = Get-Process -Id $RootProcessId -ErrorAction SilentlyContinue
        $runningChildren = @($children | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($null -eq $root -and $runningChildren.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }
}

function Invoke-Kanka([string] $Name, [string[]] $Arguments) {
    Require-NoKankaProcess
    Push-Location $WorldPath
    try {
        # The installer launcher may hand work to a child process. Wait for that child before another operation can acquire the ledger.
        return Invoke-Checked $Name {
            $outputPath = Join-Path $evidence "$Name.stdout.txt"
            $errorPath = Join-Path $evidence "$Name.stderr.txt"
            $process = Start-Process -FilePath $ToolPath -ArgumentList $Arguments -WorkingDirectory $WorldPath -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath -NoNewWindow
            Wait-KankaExit $process.Id
            Get-Content -LiteralPath $outputPath,$errorPath -ErrorAction SilentlyContinue
            if ($process.ExitCode -ne 0) { throw "git-kanka exited with code $($process.ExitCode)." }
        }
    }
    finally { Pop-Location }
}

function Require-ZeroPlan([string] $Name) {
    $plan = Invoke-Kanka $Name @('plan')
    if ($plan -notmatch '(?m)^0 API operations planned\.\r?$') { throw "Expected a zero-operation plan for $Name." }
}

function Write-Fixture([string] $Identifier, [string] $Category, [string] $Name, [string] $Body, [bool] $Private) {
    $directory = Join-Path $WorldPath "world\$($Category)s\$Identifier"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $privateText = if ($Private) { 'true' } else { 'false' }
    @"
---
category: $Category
id: $Identifier
name: $Name
publish: true
tags:
- $($fixtureIds[2])
type: ""
visibility:
  private: $privateText
---

$Body
"@ | Set-Content -LiteralPath (Join-Path $directory 'index.md') -NoNewline
}

function Write-FixtureAttachments([string] $Identifier, [string] $Target) {
    $directory = Join-Path $WorldPath "world\\locations\\$Identifier"
    @"
$Identifier-property:
  id: $Identifier-property
  name: Certification marker
  private: true
  publish: true
  type: text
  value: initial
"@ | Set-Content -LiteralPath (Join-Path $directory 'properties.yml') -NoNewline
    @"
$Identifier-relation:
  attitude: 10
  id: $Identifier-relation
  publish: true
  relation: verifies
  target: $Target
  visibility: all
"@ | Set-Content -LiteralPath (Join-Path $directory 'relations.yml') -NoNewline
    $posts = Join-Path $directory 'posts'
    New-Item -ItemType Directory -Force -Path $posts | Out-Null
    @"
---
id: $Identifier-post
name: Certification post
publish: true
visibility: all
---

Initial certification post.
"@ | Set-Content -LiteralPath (Join-Path $posts "$Identifier-post.md") -NoNewline
}

function Require-CleanRepository([string] $Name, [string] $Path) {
    $status = Invoke-Checked $Name { git -C $Path status --short }
    if (-not [string]::IsNullOrWhiteSpace($status)) { throw "$Name has uncommitted changes." }
}

function Write-Manifest([string] $Stage) {
    [ordered]@{
        run_id = $runId
        stage = $Stage
        fixture_ids = $fixtureIds
        world_path = $WorldPath
        updated_utc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -NoNewline
}

try {
    if (-not (Test-Path -LiteralPath $ToolPath)) { throw "Installed tool not found: $ToolPath" }
    if ($ResumeCleanup -and -not (Test-Path -LiteralPath $manifestPath)) { throw "No manifest exists for $runId; refusing to guess fixture identifiers." }
    Require-CleanRepository 'source-status' $repositoryRoot
    Require-CleanRepository 'world-status' $WorldPath
    Write-Evidence 'environment.json' (([ordered]@{ run_id = $runId; tool = (& $ToolPath help 2>&1 | Out-String).Trim(); windows = [Environment]::OSVersion.VersionString; git = (git --version); utc = [DateTime]::UtcNow.ToString('O') } | ConvertTo-Json -Compress))

    if (-not $SkipQualityGates) {
        Push-Location $repositoryRoot
        try {
            Invoke-Checked 'restore' { dotnet restore --locked-mode }
            Invoke-Checked 'build' { dotnet build -c Release --no-restore }
            Invoke-Checked 'format' { dotnet format --no-restore --verify-no-changes }
            Invoke-Checked 'test' { dotnet test -c Release --no-build -p:CollectCoverage=true -p:CoverletOutputFormat=teamcity -p:Threshold=80 -p:ThresholdType=line -p:ThresholdStat=total }
            Invoke-Checked 'pack' { dotnet pack src/KankaGitSync -c Release --no-build -o artifacts/packages }
        }
        finally { Pop-Location }
    }

    if (-not $RunLive) { Write-Evidence 'result.txt' 'Preflight passed. Re-run with -RunLive to mutate the disposable campaign.'; exit 0 }
    Write-Manifest 'baseline'
    Require-ZeroPlan 'baseline-plan'
    if ($ResumeCleanup) {
        Write-Manifest 'cleanup'
        foreach ($identifier in $fixtureIds) { Invoke-Kanka "delete-$identifier" @('delete', $identifier) | Out-Null }
        Invoke-Checked 'fixture-delete-commit' { git -C $WorldPath add world; git -C $WorldPath commit -m "Delete $runId fixtures" }
        Invoke-Kanka 'fixture-delete-push' @('push', '--allow-delete', '--approve-privacy') | Out-Null
        Require-ZeroPlan 'cleanup-zero-plan'
        Write-Manifest 'complete'
        Write-Evidence 'result.txt' 'Resumed cleanup passed.'
        exit 0
    }
    Write-Fixture $fixtureIds[2] 'tag' "Release certification $runId" 'Temporary certification tag.' $false
    Write-Fixture $fixtureIds[0] 'location' "Release certification A $runId" "Linked to [[$($fixtureIds[1])|fixture B]]." $false
    Write-Fixture $fixtureIds[1] 'location' "Release certification B $runId" "Linked to [[$($fixtureIds[0])|fixture A]]." $true
    Write-FixtureAttachments $fixtureIds[0] $fixtureIds[1]
    Write-Manifest 'fixtures-authored'
    Invoke-Checked 'fixture-commit' { git -C $WorldPath add world; git -C $WorldPath commit -m "Add $runId fixtures" }
    Invoke-Kanka 'fixture-validate' @('validate') | Out-Null
    Invoke-Kanka 'fixture-push' @('push', '--approve-privacy') | Out-Null
    Require-ZeroPlan 'fixture-zero-plan'
    Write-Manifest 'fixtures-published'

    $first = Join-Path $WorldPath "world\\locations\\$($fixtureIds[0])"
    (Get-Content -Raw -LiteralPath (Join-Path $first 'index.md')).Replace('fixture B]].', 'fixture B, updated]].') | Set-Content -LiteralPath (Join-Path $first 'index.md') -NoNewline
    (Get-Content -Raw -LiteralPath (Join-Path $first 'properties.yml')).Replace('value: initial', 'value: updated') | Set-Content -LiteralPath (Join-Path $first 'properties.yml') -NoNewline
    (Get-Content -Raw -LiteralPath (Join-Path $first 'relations.yml')).Replace('attitude: 10', 'attitude: 20') | Set-Content -LiteralPath (Join-Path $first 'relations.yml') -NoNewline
    (Get-Content -Raw -LiteralPath (Join-Path $first 'posts\' + $fixtureIds[0] + '-post.md')).Replace('Initial certification post.', 'Updated certification post.') | Set-Content -LiteralPath (Join-Path $first 'posts\' + $fixtureIds[0] + '-post.md') -NoNewline
    (Get-Content -Raw -LiteralPath (Join-Path $WorldPath "world\\locations\\$($fixtureIds[1])\\index.md")).Replace('private: true', 'private: false') | Set-Content -LiteralPath (Join-Path $WorldPath "world\\locations\\$($fixtureIds[1])\\index.md") -NoNewline
    Invoke-Checked 'fixture-update-commit' { git -C $WorldPath add world; git -C $WorldPath commit -m "Update $runId fixtures" }
    Invoke-Kanka 'fixture-update-push' @('push', '--approve-privacy') | Out-Null
    Require-ZeroPlan 'fixture-update-zero-plan'
    Write-Manifest 'fixtures-updated'

    Remove-Item -LiteralPath (Join-Path $first 'posts\' + $fixtureIds[0] + '-post.md')
    Invoke-Checked 'missing-file-commit' { git -C $WorldPath add world; git -C $WorldPath commit -m "Remove local $runId post without tombstone" }
    Require-ZeroPlan 'missing-file-zero-plan'
    Write-Manifest 'missing-file-verified'

    if (-not $KeepFixtures) {
        foreach ($identifier in $fixtureIds) { Invoke-Kanka "delete-$identifier" @('delete', $identifier) | Out-Null }
        Invoke-Checked 'fixture-delete-commit' { git -C $WorldPath add world; git -C $WorldPath commit -m "Delete $runId fixtures" }
        Invoke-Kanka 'fixture-delete-push' @('push', '--allow-delete', '--approve-privacy') | Out-Null
        Require-ZeroPlan 'cleanup-zero-plan'
        Write-Manifest 'complete'
    }
    Write-Evidence 'result.txt' 'Live create, update, privacy, circular-reference, missing-file, and explicit-cleanup matrix passed. Proxy recovery and two-clone conflict phases require their dedicated runner modes.'
}
finally {
    if ($null -ne $proxyProcess) { Stop-Process -Id $proxyProcess -Force -ErrorAction SilentlyContinue }
    if ($null -ne $certificateThumbprint) { Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$certificateThumbprint" -Force -ErrorAction SilentlyContinue }
}
