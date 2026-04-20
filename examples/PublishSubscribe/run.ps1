$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$billingSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PublishSubscribe.BillingSubscriber/ServiceConnect.Examples.PublishSubscribe.BillingSubscriber.csproj'
$analyticsSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PublishSubscribe.AnalyticsSubscriber/ServiceConnect.Examples.PublishSubscribe.AnalyticsSubscriber.csproj'
$publisherProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PublishSubscribe.Publisher/ServiceConnect.Examples.PublishSubscribe.Publisher.csproj'
$billingProcess = $null
$analyticsProcess = $null
$publisherJob = $null

function Wait-ForSubscribersReady {
    $timeout = 30
    $elapsed = 0
    $billingReady = $false
    $analyticsReady = $false

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and (Select-String -Path $OUTPUT_LOG -Pattern "READY:billing-subscriber" -Quiet) -and -not $billingReady) {
            $billingReady = $true
        }
        if ((Test-Path $OUTPUT_LOG) -and (Select-String -Path $OUTPUT_LOG -Pattern "READY:analytics-subscriber" -Quiet) -and -not $analyticsReady) {
            $analyticsReady = $true
        }

        if ($billingReady -and $analyticsReady) {
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
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:billing-subscriber:processed order-100' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:analytics-subscriber:processed order-100' -Quiet)) {
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
    $billingProcess = Start-Process dotnet -ArgumentList @('run', '--project', $billingSubscriberProject) -PassThru -NoNewWindow -RedirectStandardOutput $OUTPUT_LOG -RedirectStandardError $OUTPUT_LOG
    $analyticsProcess = Start-Process dotnet -ArgumentList @('run', '--project', $analyticsSubscriberProject) -PassThru -NoNewWindow -RedirectStandardOutput $OUTPUT_LOG -RedirectStandardError $OUTPUT_LOG -Append

    if (-not (Wait-ForSubscribersReady)) {
        throw "Subscribers did not become ready within 30 seconds"
    }

    $publisherJob = Start-Job -ScriptBlock {
        dotnet run --project $using:publisherProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $publisherJob | Wait-Job | Remove-Job -Force

    if (-not (Wait-ForSubscriberSuccess)) {
        throw 'Subscribers did not both process order-100 within 30 seconds'
    }
}
finally {
    if ($null -ne $billingProcess -and -not $billingProcess.HasExited) {
        Stop-Process -Id $billingProcess.Id -Force -ErrorAction SilentlyContinue
        $billingProcess.WaitForExit()
    }
    if ($null -ne $analyticsProcess -and -not $analyticsProcess.HasExited) {
        Stop-Process -Id $analyticsProcess.Id -Force -ErrorAction SilentlyContinue
        $analyticsProcess.WaitForExit()
    }
}
