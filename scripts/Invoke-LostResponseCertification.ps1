[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorldPath,
    [Parameter(Mandatory)] [string] $ApiPath,
    [Parameter(Mandatory)] [ValidateSet('POST', 'PATCH')] [string] $Method,
    [string] $ToolPath = (Join-Path $env:LOCALAPPDATA 'Programs\KankaGitSync\git-kanka.exe')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root ('artifacts\release-certification\lost-response-' + (Get-Date -Format 'yyyyMMddHHmmss'))
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$proxy = $null
$locationPushed = $false

function Write-SafeEvidence([string] $Name, [string] $Value) {
    $safe = $Value -replace '(?im)(authorization:\s*bearer\s+)[^\s]+', '$1[redacted]' -replace '(?im)(KANKA_(?:API_)?TOKEN=)[^\s]+', '$1[redacted]'
    Set-Content -LiteralPath (Join-Path $evidence $Name) -Value $safe -NoNewline
}

function Invoke-Recorded([string] $Name, [scriptblock] $Action, [bool] $ExpectedFailure = $false) {
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $text = (& $Action 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    catch { $text = $_ | Out-String; $exitCode = 1 }
    finally { $ErrorActionPreference = $savedPreference }
    Write-SafeEvidence "$Name.txt" $text
    if (($exitCode -ne 0) -ne $ExpectedFailure) { throw "$Name returned unexpected exit code $exitCode." }
    return $text
}

function Require-ZeroPlan {
    $plan = Invoke-Recorded 'post-recovery-plan' { & $ToolPath plan }
    if ($plan -notmatch '(?m)^0 API operations planned\.\r?$') { throw 'Recovery did not reconcile to a zero-operation plan.' }
}

try {
    if (Get-Process git-kanka -ErrorAction SilentlyContinue) { throw 'A git-kanka process is already active; refusing to contend for the operation ledger.' }
    if ((git -C $WorldPath status --porcelain).Count -ne 0) { throw 'World repository has uncommitted changes.' }
    Push-Location $WorldPath
    $locationPushed = $true
    $proxy = & (Join-Path $PSScriptRoot 'Start-KankaFaultProxy.ps1') -Method $Method -Path $ApiPath -EvidenceDirectory $evidence
    $env:HTTP_PROXY = $proxy.Proxy
    $env:HTTPS_PROXY = $proxy.Proxy
    Invoke-Recorded 'push-lost-response' { & $ToolPath push --approve-privacy } $true | Out-Null
    Remove-Item Env:\HTTP_PROXY,Env:\HTTPS_PROXY -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $proxy.Evidence)) { throw 'The proxy did not confirm an origin response before suppression.' }
    Invoke-Recorded 'doctor-blocked' { & $ToolPath doctor } $true | Out-Null
    Invoke-Recorded 'subsequent-push-blocked' { & $ToolPath push --approve-privacy } $true | Out-Null
    Invoke-Recorded 'fetch-recovery' { & $ToolPath fetch --full } | Out-Null
    Invoke-Recorded 'reconcile-live' { git merge --no-edit kanka/live } | Out-Null
    Invoke-Recorded 'doctor-acknowledge' { & $ToolPath doctor --acknowledge-recovery } | Out-Null
    Require-ZeroPlan
    Invoke-Recorded 'world-push' { git push origin main } | Out-Null
    Write-SafeEvidence 'result.txt' 'Lost-response recovery passed: origin response confirmed, replay blocked, live state reconciled, acknowledgement completed, and plan is zero.'
}
finally {
    if ($locationPushed) { Pop-Location }
    Remove-Item Env:\HTTP_PROXY,Env:\HTTPS_PROXY -ErrorAction SilentlyContinue
    if ($null -ne $proxy) {
        Stop-Process -Id $proxy.ProcessId -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$($proxy.CertificateThumbprint)" -Force -ErrorAction SilentlyContinue
        $removed = -not (Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($proxy.CertificateThumbprint)")
        Write-SafeEvidence 'certificate-cleanup.txt' ("thumbprint={0};removed={1}" -f $proxy.CertificateThumbprint, $removed)
    }
}
