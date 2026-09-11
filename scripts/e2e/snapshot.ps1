<#
.SYNOPSIS
    Dispara un tick del monitor y vuelca su estado a docs/e2e/evidence/.

.DESCRIPTION
    POST /schedule/trigger contra MonitorApi, espera unos segundos a que MonitorWorkflow
    corra, y vuelca GET /patches + GET /patches/{ns}/{type}/{patchId} de cada patch listado
    a docs/e2e/evidence/<timestamp>-<Label>.json. Es la evidencia del paso 10 del spec 09.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Label,

    [string]$MonitorApiUrl = "http://localhost:5100",

    [int]$WaitSeconds = 10
)

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$evidenceDir = Join-Path $repoRoot "docs/e2e/evidence"
New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

Invoke-RestMethod -Method Post -Uri "$MonitorApiUrl/schedule/trigger" | Out-Null

Start-Sleep -Seconds $WaitSeconds

$list = Invoke-RestMethod -Method Get -Uri "$MonitorApiUrl/patches"

$details = @()
foreach ($patch in $list.patches) {
    $detail = Invoke-RestMethod -Method Get `
        -Uri "$MonitorApiUrl/patches/$($patch.namespace)/$($patch.workflowType)/$($patch.patchId)"
    $details += $detail
}

$snapshot = [ordered]@{
    label     = $Label
    timestamp = (Get-Date).ToUniversalTime().ToString("o")
    list      = $list
    details   = $details
}

$fileTimestamp = Get-Date -Format "yyyyMMddHHmmss"
$filePath = Join-Path $evidenceDir "$fileTimestamp-$Label.json"

$snapshot | ConvertTo-Json -Depth 10 | Set-Content -Path $filePath -Encoding utf8

Write-Host "Snapshot guardado en $filePath"
$filePath
