<#
.SYNOPSIS
    Drena órdenes de ReleaseOrderDemo aprobando su decisión de liberación.

.DESCRIPTION
    POST /orders/{id}/release/decision con {"approved": true} para cada id (la Signal, no
    el Update). Espera a que cada orden confirme GetStatus == Completed (o Failed /
    compensado) antes de devolver el control, porque los timers del workflow
    (Workflow.DelayAsync(5s) y (10s)) hacen que drenar tarde ~15-20s de reloj real.
#>
param(
    [Parameter(Mandatory = $true)]
    [int[]]$OrderIds,

    [string]$BaseUrl = "http://localhost:5000",

    [int]$TimeoutSeconds = 60
)

foreach ($orderId in $OrderIds) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl/orders/$orderId/release/decision" `
        -ContentType "application/json" -Body (@{ approved = $true } | ConvertTo-Json) | Out-Null

    $elapsed = 0
    $status = $null
    do {
        Start-Sleep -Seconds 2
        $elapsed += 2
        $status = Invoke-RestMethod -Method Get -Uri "$BaseUrl/orders/$orderId/status"
    } while ($status.state -eq "Running" -and $elapsed -lt $TimeoutSeconds)

    if ($status.state -eq "Running") {
        Write-Warning "Orden $orderId sigue Running tras $TimeoutSeconds s (status: $($status.status))."
    }
    else {
        Write-Host "Orden $orderId drenada: $($status.status) ($($status.state))."
    }
}
