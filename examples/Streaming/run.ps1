$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$receiverProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Streaming.Receiver/ServiceConnect.Examples.Streaming.Receiver.csproj'
$uploaderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.Streaming.Uploader/ServiceConnect.Examples.Streaming.Uploader.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$queueName = "streaming-receiver-$runId"
$receiverProcess = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:streaming-receiver$' -Quiet)) {
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
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:streaming-uploader:sent 3 chunks for demo-document.txt$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:streaming-receiver:received demo-document.txt with 103 bytes$' -Quiet)) {
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

    $receiverProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_QUEUE_NAME='$queueName'; dotnet run --project '$receiverProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Streaming receiver did not become ready within 30 seconds'
    }

    $env:SC_EXAMPLES_ENDPOINT_NAME = $queueName
    dotnet run --project $uploaderProject 2>&1 | Out-File -FilePath $OUTPUT_LOG -Append
    Remove-Item Env:SC_EXAMPLES_ENDPOINT_NAME -ErrorAction SilentlyContinue

    if (-not (Wait-ForSuccess)) {
        throw 'Streaming run did not produce the expected success lines within 30 seconds'
    }
}
finally {
    if ($null -ne $receiverProcess -and -not $receiverProcess.HasExited) {
        Stop-Process -Id $receiverProcess.Id -Force -ErrorAction SilentlyContinue
        $receiverProcess.WaitForExit()
    }
}
