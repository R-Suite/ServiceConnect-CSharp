$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$consumerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Filters.Consumer/ServiceConnect.Examples.Filters.Consumer.csproj'
$senderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Filters.Sender/ServiceConnect.Examples.Filters.Sender.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$queueName = "filters-consumer-$runId"
$consumerProcess = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:filters-consumer$' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

function Wait-ForSuccess {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:filters-consumer:trace trace-001$' -Quiet)) {
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

    $consumerProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_QUEUE_NAME='$queueName'; dotnet run --project '$consumerProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Filters consumer did not become ready within 30 seconds'
    }

    $env:SC_EXAMPLES_QUEUE_NAME = $queueName
    dotnet run --project $senderProject 2>&1 | Out-File -FilePath $OUTPUT_LOG -Append
    Remove-Item Env:SC_EXAMPLES_QUEUE_NAME -ErrorAction SilentlyContinue

    if (-not (Wait-ForSuccess)) {
        throw 'Filters run did not produce the expected consumer success line within 30 seconds'
    }
}
finally {
    if ($null -ne $consumerProcess -and -not $consumerProcess.HasExited) {
        Stop-Process -Id $consumerProcess.Id -Force -ErrorAction SilentlyContinue
        $consumerProcess.WaitForExit()
    }
}
