$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$consumerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Aggregator.Consumer/ServiceConnect.Examples.Aggregator.Consumer.csproj'
$producerAProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Aggregator.ProducerA/ServiceConnect.Examples.Aggregator.ProducerA.csproj'
$producerBProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Aggregator.ProducerB/ServiceConnect.Examples.Aggregator.ProducerB.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$queueName = "aggregator-consumer-$runId"
$databaseName = "aggregator_consumer_$($runId.Replace('-', '_'))"
$correlationId = [Guid]::NewGuid().ToString()
$consumerProcess = $null
$producerAJob = $null
$producerBJob = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:aggregator-consumer$' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

function Wait-ForCompletion {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:aggregator-producer-a:sent ProducerA/10$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:aggregator-producer-b:sent ProducerB/15$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:aggregator-consumer:combined total 25 from 2 slices$' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

try {
    Start-ExampleDependencies
    '' | Set-Content -Path $OUTPUT_LOG

    $consumerProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_QUEUE_NAME='$queueName'; `$env:SC_EXAMPLES_DATABASE_NAME='$databaseName'; dotnet run --project '$consumerProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Aggregator consumer did not become ready within 30 seconds'
    }

    $producerAJob = Start-Job -ScriptBlock {
        $env:SC_EXAMPLES_QUEUE_NAME = $using:queueName
        $env:SC_EXAMPLES_CORRELATION_ID = $using:correlationId
        dotnet run --project $using:producerAProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $producerBJob = Start-Job -ScriptBlock {
        $env:SC_EXAMPLES_QUEUE_NAME = $using:queueName
        $env:SC_EXAMPLES_CORRELATION_ID = $using:correlationId
        dotnet run --project $using:producerBProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $producerAJob | Wait-Job | Remove-Job -Force
    $producerAJob = $null
    $producerBJob | Wait-Job | Remove-Job -Force
    $producerBJob = $null

    if (-not (Wait-ForCompletion)) {
        throw 'Aggregator run did not produce the combined total within 30 seconds'
    }
}
finally {
    if ($null -ne $producerAJob) {
        Remove-Job -Job $producerAJob -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $producerBJob) {
        Remove-Job -Job $producerBJob -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $consumerProcess -and -not $consumerProcess.HasExited) {
        Stop-Process -Id $consumerProcess.Id -Force -ErrorAction SilentlyContinue
        $consumerProcess.WaitForExit()
    }
}
