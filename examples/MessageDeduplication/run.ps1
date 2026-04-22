$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$consumerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.MessageDeduplication.Consumer/ServiceConnect.Examples.MessageDeduplication.Consumer.csproj'
$senderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.MessageDeduplication.Sender/ServiceConnect.Examples.MessageDeduplication.Sender.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$queueName = "dedup-consumer-$runId"
$senderQueue = "dedup-sender-$runId"
$mongoDb = "dedup-sample-$runId"
$consumerProcess = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:dedup-consumer$' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

function Wait-ForBlockedRedelivery {
    $timeout = 20
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if (Test-Path $OUTPUT_LOG) {
            $handled = (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:dedup-consumer:handled').Count
            if ($handled -ge 1) {
                Start-Sleep -Seconds 2
                $final = (Select-String -Path $OUTPUT_LOG -Pattern 'SUCCESS:dedup-consumer:handled').Count
                if ($final -eq 1) {
                    return $true
                }
                throw "FAIL: handler ran $final times - expected 1 (filter did not block redelivery)"
            }
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    throw 'FAIL: handler never invoked within 20s'
}

try {
    Start-ExampleDependencies
    '' | Set-Content -Path $OUTPUT_LOG

    $consumerArgs = "`$env:SC_EXAMPLES_QUEUE_NAME='$queueName'; " +
        "`$env:SC_DEDUP_MONGO_DB='$mongoDb'; " +
        "dotnet run --project '$consumerProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append"
    $consumerProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', $consumerArgs) -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Consumer did not become ready within 30 seconds'
    }

    $env:SC_EXAMPLES_QUEUE_NAME = $senderQueue
    $env:SC_EXAMPLES_CONSUMER_QUEUE = $queueName
    $env:SC_DEDUP_MONGO_DB = $mongoDb
    try {
        dotnet run --project $senderProject 2>&1 | Out-File -FilePath $OUTPUT_LOG -Append
    }
    finally {
        Remove-Item Env:SC_EXAMPLES_QUEUE_NAME -ErrorAction SilentlyContinue
        Remove-Item Env:SC_EXAMPLES_CONSUMER_QUEUE -ErrorAction SilentlyContinue
        Remove-Item Env:SC_DEDUP_MONGO_DB -ErrorAction SilentlyContinue
    }

    Wait-ForBlockedRedelivery | Out-Null
    Write-Host 'SUCCESS: handler invoked exactly once; filter blocked the redelivery.'
}
finally {
    if ($null -ne $consumerProcess -and -not $consumerProcess.HasExited) {
        Stop-Process -Id $consumerProcess.Id -Force -ErrorAction SilentlyContinue
        $consumerProcess.WaitForExit()
    }
}
