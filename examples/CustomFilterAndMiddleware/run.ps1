$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$consumerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer/ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.csproj'
$senderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender/ServiceConnect.Examples.CustomFilterAndMiddleware.Sender.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$consumerProcess = $null

function Wait-ForRabbitMQ {
    $maxAttempts = 60
    $attempt = 0

    while ($attempt -lt $maxAttempts) {
        try {
            docker exec custom-filter-rabbit rabbitmq-diagnostics -q ping 2>$null
            if ($LASTEXITCODE -eq 0) { return $true }
        } catch {}

        Start-Sleep -Seconds 1
        $attempt++
    }

    return $false
}

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:custom-filter-consumer$' -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

try {
    docker run -d --rm --name custom-filter-rabbit -p 5672:5672 rabbitmq:3.13-management

    if (-not (Wait-ForRabbitMQ)) {
        throw 'RabbitMQ did not become ready within 60 seconds'
    }

    '' | Set-Content -Path $OUTPUT_LOG

    $consumerArgs = "dotnet run --project '$consumerProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append"
    $consumerProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', $consumerArgs) -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Consumer did not become ready within 30 seconds'
    }

    dotnet run --project $senderProject 2>&1 | Out-File -FilePath $OUTPUT_LOG -Append

    Start-Sleep -Seconds 5
}
finally {
    if ($null -ne $consumerProcess -and -not $consumerProcess.HasExited) {
        Stop-Process -Id $consumerProcess.Id -Force -ErrorAction SilentlyContinue
        $consumerProcess.WaitForExit()
    }

    docker rm -f custom-filter-rabbit 2>$null | Out-Null

    Write-Host '--- Consumer log ---'
    if (Test-Path $OUTPUT_LOG) { Get-Content $OUTPUT_LOG }
}
