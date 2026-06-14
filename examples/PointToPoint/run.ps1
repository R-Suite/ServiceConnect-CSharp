$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$consumerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PointToPoint.Consumer/ServiceConnect.Examples.PointToPoint.Consumer.csproj'
$senderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.PointToPoint.Sender/ServiceConnect.Examples.PointToPoint.Sender.csproj'
$consumerProcess = $null

try {
    Start-ExampleDependencies
    try {
        docker compose -f "$PSScriptRoot/../docker-compose.yml" exec -T rabbitmq rabbitmqctl purge_queue point-to-point-consumer | Out-Null
    }
    catch {
    }
    $consumerProcess = Start-Process dotnet -ArgumentList @('run', '--project', $consumerProject) -PassThru -NoNewWindow
    Start-Sleep -Seconds 5
    dotnet run --project $senderProject
    Start-Sleep -Seconds 5
}
finally {
    if ($null -ne $consumerProcess -and -not $consumerProcess.HasExited) {
        Stop-Process -Id $consumerProcess.Id -Force
        $consumerProcess.WaitForExit()
    }
}
