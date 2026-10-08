[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorldPath,
    [Parameter(Mandatory)] [string] $FixturePath,
    [string] $ToolPath = (Join-Path $env:LOCALAPPDATA 'Programs\KankaGitSync\git-kanka.exe')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root ('artifacts\release-certification\two-clone-' + (Get-Date -Format 'yyyyMMddHHmmss'))
$left = Join-Path $run 'owner-clone'
$right = Join-Path $run 'writer-clone'
New-Item -ItemType Directory -Force -Path $run | Out-Null
function Invoke-Tool([string] $Name, [string] $Path, [string[]] $Arguments, [bool] $ExpectedFailure = $false) {
    Push-Location $Path
    try {
        $text = (& $ToolPath @Arguments 2>&1 | Out-String)
        Set-Content -LiteralPath (Join-Path $run "$Name.txt") -Value $text -NoNewline
        if (($LASTEXITCODE -ne 0) -ne $ExpectedFailure) { throw "$Name returned an unexpected exit code." }
        return $text
    } finally { Pop-Location }
}
$remote = (git -C $WorldPath remote get-url origin).Trim()
git clone $remote $left
git clone $remote $right
git -C $left config user.name 'Release certification'
git -C $left config user.email 'release-certification@localhost'
git -C $right config user.name 'Release certification'
git -C $right config user.email 'release-certification@localhost'
Copy-Item -LiteralPath (Join-Path $WorldPath '.env') -Destination (Join-Path $left '.env')
Copy-Item -LiteralPath (Join-Path $WorldPath '.env') -Destination (Join-Path $right '.env')

foreach ($clone in @($left, $right)) {
    Invoke-Tool "bootstrap-fetch-$(Split-Path $clone -Leaf)" $clone @('fetch') | Out-Null
    git -C $clone merge --ff-only kanka/live
    if ($LASTEXITCODE -ne 0) { throw "Could not integrate the initial Kanka state in $clone." }
}

# Caller prepares a unique fixture. Owner publishes a structured field; concurrent writer must be blocked before mutation.
(Get-Content -Raw -LiteralPath (Join-Path $left $FixturePath)).Replace('title: ""', 'title: Owner certification') | Set-Content -LiteralPath (Join-Path $left $FixturePath) -NoNewline
git -C $left add world; git -C $left commit -m 'Owner certification update'
Invoke-Tool 'owner-push' $left @('push', '--approve-privacy') | Out-Null
(Get-Content -Raw -LiteralPath (Join-Path $right $FixturePath)).Replace('title: ""', 'title: Concurrent certification') | Set-Content -LiteralPath (Join-Path $right $FixturePath) -NoNewline
git -C $right add world; git -C $right commit -m 'Concurrent certification update'
$blocked = Invoke-Tool 'writer-blocked' $right @('push', '--approve-privacy') $true
if ($blocked -notmatch 'unintegrated remote changes') { throw 'Concurrent writer was not blocked by remote integration protection.' }
Set-Content -LiteralPath (Join-Path $run 'result.txt') -Value 'Owner/write conflict block passed. Continue with explicit merge and prose/privacy variants.' -NoNewline
