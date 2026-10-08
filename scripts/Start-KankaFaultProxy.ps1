[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Z]+$')] [string] $Method,
    [Parameter(Mandatory)] [ValidatePattern('^/1\.0/campaigns/[1-9][0-9]*/')] [string] $Path,
    [Parameter(Mandatory)] [string] $EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSCommandPath
$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) { throw 'Python is required to provision the isolated mitmproxy environment.' }

$venv = Join-Path $EvidenceDirectory 'proxy-venv'
$conf = Join-Path $EvidenceDirectory 'proxy-ca'
$eventPath = Join-Path $EvidenceDirectory 'lost-response.json'
& $python.Source -m venv $venv
$venvPython = Join-Path $venv 'Scripts\python.exe'
& $venvPython -m pip install --disable-pip-version-check --quiet 'mitmproxy==11.0.2'
if ($LASTEXITCODE -ne 0) { throw 'Could not install mitmproxy into the ignored certification directory.' }

New-Item -ItemType Directory -Force -Path $conf | Out-Null
$env:KANKA_FAULT_METHOD = $Method
$env:KANKA_FAULT_PATH = $Path
$env:KANKA_FAULT_EVIDENCE = $eventPath
$mitmdump = Join-Path $venv 'Scripts\mitmdump.exe'
$process = Start-Process -FilePath $mitmdump -ArgumentList @('--quiet', '--listen-host', '127.0.0.1', '--listen-port', '18765', '--set', "confdir=$conf", '-s', (Join-Path $scriptRoot 'kanka_fault_proxy.py')) -PassThru -WindowStyle Hidden

$certificate = Join-Path $conf 'mitmproxy-ca-cert.cer'
for ($attempt = 0; $attempt -lt 30 -and -not (Test-Path -LiteralPath $certificate); $attempt++) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path -LiteralPath $certificate)) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw 'mitmproxy did not create its temporary certificate.'
}
$imported = Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\Root
[pscustomobject]@{
    ProcessId = $process.Id
    Proxy = 'http://127.0.0.1:18765'
    CertificateThumbprint = $imported.Thumbprint
    Evidence = $eventPath
}
