<#
.SYNOPSIS
    Crea N órdenes en ReleaseOrderDemo y las libera, sin mandar decisión.

.DESCRIPTION
    POST /orders + POST /orders/{id}/release por cada orden. Las órdenes quedan Running,
    en "Waiting for release decision", sin marker si el worker todavía corre código
    pre-patch. Devuelve la lista de orderIds creados por el pipeline.
#>
param(
    [Parameter(Mandatory = $true)]
    [int]$Count,

    [string]$BaseUrl = "http://localhost:5000"
)

$orderIds = @()

for ($i = 1; $i -le $Count; $i++) {
    $body = @{
        orderCode = "e2e-$(Get-Date -Format 'yyyyMMddHHmmssfff')-$i"
        quantity  = 1
        productId = 1
        amount    = 10.0
        address   = "E2E test address"
    } | ConvertTo-Json

    $order = Invoke-RestMethod -Method Post -Uri "$BaseUrl/orders" `
        -ContentType "application/json" -Body $body
    $orderId = $order.orderId

    Invoke-RestMethod -Method Post -Uri "$BaseUrl/orders/$orderId/release" | Out-Null

    Write-Host "Orden $orderId creada y liberada (sin decisión)."
    $orderIds += $orderId
}

$orderIds
