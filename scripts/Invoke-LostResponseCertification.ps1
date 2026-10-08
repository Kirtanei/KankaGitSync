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
function Save-Result([string] $Name, [scriptblock] $Action, [bool] $ExpectedFailure = $false) {
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $text = (& $Action 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    catch {
        $text = $_ | Out-String
        $exitCode = 1
    }
    finally { $ErrorActionPreference = $savedPreference }
    $text = $text -replace '(?im)(authorization:\s*bearer\s+)[^\s]+', '$1[redacted]'
    Set-Content -LiteralPath (Join-Path $evidence "$Name.txt") -Value $text -NoNewline
    if (($exitCode -ne 0) -ne $ExpectedFailure) { throw "$Name returned an unexpected exit code." }
    return $text
}
try {
    if (Get-Process git-kanka -ErrorAction SilentlyContinue) { throw 'A git-kanka process is already active.' }
    Push-Location $WorldPath
    $proxy = & (Join-Path $PSScriptRoot 'Start-KankaFaultProxy.ps1') -Method $Method -Path $ApiPath -EvidenceDirectory $evidence
    $env:HTTP_PROXY = $proxy.Proxy
    $env:HTTPS_PROXY = $proxy.Proxy
    Save-Result 'push-lost-response' { & $ToolPath push --approve-privacy } $true | Out-Null
    Remove-Item Env:\HTTP_PROXY,Env:\HTTPS_PROXY -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $proxy.Evidence)) { throw 'The proxy did not confirm an origin response before suppression.' }
    Save-Result 'doctor-blocked' { & $ToolPath doctor } $true | Out-Null
    Save-Result 'fetch-recovery' { & $ToolPath fetch } | Out-Null
    Save-Result 'doctor-acknowledge' { & $ToolPath doctor --acknowledge-recovery } | Out-Null
    $plan = Save-Result 'post-recovery-plan' { & $ToolPath plan }
    if ($plan -notmatch '(?m)^0 API operations planned\.\r?$') { throw 'Recovery did not reconcile to a zero-operation plan.' }
    Set-Content -LiteralPath (Join-Path $evidence 'result.txt') -Value 'Lost-response recovery passed.' -NoNewline
}
finally {
    if ((Get-Location).Path -eq $WorldPath) { Pop-Location }
    Remove-Item Env:\HTTP_PROXY,Env:\HTTPS_PROXY -ErrorAction SilentlyContinue
    if ($null -ne $proxy) {
        Stop-Process -Id $proxy.ProcessId -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$($proxy.CertificateThumbprint)" -Force -ErrorAction SilentlyContinue
    }
}
