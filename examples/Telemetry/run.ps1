$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$billingSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Telemetry.BillingSubscriber/ServiceConnect.Examples.Telemetry.BillingSubscriber.csproj'
$analyticsSubscriberProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber/ServiceConnect.Examples.Telemetry.AnalyticsSubscriber.csproj'
$publisherProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Telemetry.Publisher/ServiceConnect.Examples.Telemetry.Publisher.csproj'
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
            (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:telemetry-publisher:published order' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'BILLING:received:' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern 'ANALYTICS:received:' -Quiet)) {
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
        throw 'Subscribers did not both receive the order within 30 seconds'
    }

    # Trace-correlation assertions ----------------------------------------
    $pubLine = (Select-String -Path $OUTPUT_LOG -Pattern '^TRACE:telemetry-publisher:[A-Za-z.]+:').Line | Select-Object -First 1
    $billLine = (Select-String -Path $OUTPUT_LOG -Pattern '^TRACE:billing-subscriber:[A-Za-z.]+:').Line | Select-Object -First 1
    $analyticsLine = (Select-String -Path $OUTPUT_LOG -Pattern '^TRACE:analytics-subscriber:[A-Za-z.]+:').Line | Select-Object -First 1

    if (-not $pubLine -or -not $billLine -or -not $analyticsLine) {
        throw "FAIL: missing TRACE: line for one or more processes"
    }

    # TRACE:<endpoint>:<op>:<trace>:<span>:<parent>
    $pubParts = $pubLine -split ':'
    $billParts = $billLine -split ':'
    $analyticsParts = $analyticsLine -split ':'

    $pubTrace = $pubParts[3]
    $pubSpan = $pubParts[4]
    $billTrace = $billParts[3]
    $billParent = $billParts[5]
    $analyticsTrace = $analyticsParts[3]
    $analyticsParent = $analyticsParts[5]

    if ($pubTrace -ne $billTrace -or $pubTrace -ne $analyticsTrace) {
        throw "FAIL: TraceId mismatch (pub=$pubTrace bill=$billTrace analytics=$analyticsTrace)"
    }
    if ($billParent -ne $pubSpan -or $analyticsParent -ne $pubSpan) {
        throw "FAIL: ParentSpanId mismatch (pub=$pubSpan bill=$billParent analytics=$analyticsParent)"
    }
    Write-Host "OK: trace-id correlated across publisher and both subscribers"
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
