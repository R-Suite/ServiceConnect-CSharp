$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$auditSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PolymorphicMessages.AuditSubscriber/ServiceConnect.Examples.PolymorphicMessages.AuditSubscriber.csproj'
$shippingSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PolymorphicMessages.ShippingSubscriber/ServiceConnect.Examples.PolymorphicMessages.ShippingSubscriber.csproj'
$publisherProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PolymorphicMessages.Publisher/ServiceConnect.Examples.PolymorphicMessages.Publisher.csproj'
$auditProcess = $null
$shippingProcess = $null
$publisherJob = $null

function Wait-ForSubscribersReady {
    $timeout = 30
    $elapsed = 0
    $auditReady = $false
    $shippingReady = $false

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and (Select-String -Path $OUTPUT_LOG -Pattern "READY:audit-subscriber" -Quiet) -and -not $auditReady) {
            $auditReady = $true
        }
        if ((Test-Path $OUTPUT_LOG) -and (Select-String -Path $OUTPUT_LOG -Pattern "READY:shipping-subscriber" -Quiet) -and -not $shippingReady) {
            $shippingReady = $true
        }

        if ($auditReady -and $shippingReady) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

function Wait-ForSubscriberSuccess {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:audit-subscriber:audited OrderPlaced order-42' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:audit-subscriber:audited OrderShipped order-42' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:shipping-subscriber:processed order-shipped order-42' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

$OUTPUT_LOG = Join-Path $PSScriptRoot "output.log"

try {
    Start-ExampleDependencies
    "" | Set-Content -Path $OUTPUT_LOG
    $auditProcess = Start-Process dotnet -ArgumentList @('run', '--project', $auditSubscriberProject) -PassThru -NoNewWindow -RedirectStandardOutput $OUTPUT_LOG -RedirectStandardError $OUTPUT_LOG
    $shippingProcess = Start-Process dotnet -ArgumentList @('run', '--project', $shippingSubscriberProject) -PassThru -NoNewWindow -RedirectStandardOutput $OUTPUT_LOG -RedirectStandardError $OUTPUT_LOG -Append

    if (-not (Wait-ForSubscribersReady)) {
        throw "Subscribers did not become ready within 30 seconds"
    }

    $publisherJob = Start-Job -ScriptBlock {
        dotnet run --project $using:publisherProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $publisherJob | Wait-Job | Remove-Job -Force

    if (-not (Wait-ForSubscriberSuccess)) {
        throw 'Subscribers did not observe all three expected SUCCESS lines within 30 seconds'
    }
}
finally {
    if ($null -ne $auditProcess -and -not $auditProcess.HasExited) {
        Stop-Process -Id $auditProcess.Id -Force -ErrorAction SilentlyContinue
        $auditProcess.WaitForExit()
    }
    if ($null -ne $shippingProcess -and -not $shippingProcess.HasExited) {
        Stop-Process -Id $shippingProcess.Id -Force -ErrorAction SilentlyContinue
        $shippingProcess.WaitForExit()
    }
}
