$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$catalogAProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ScatterGather.CatalogA/ServiceConnect.Examples.ScatterGather.CatalogA.csproj'
$catalogBProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ScatterGather.CatalogB/ServiceConnect.Examples.ScatterGather.CatalogB.csproj'
$requesterProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ScatterGather.Requester/ServiceConnect.Examples.ScatterGather.Requester.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$requesterQueueName = "scatter-gather-requester-$runId"
$catalogAQueueName = "scatter-gather-catalog-a-$runId"
$catalogBQueueName = "scatter-gather-catalog-b-$runId"
$searchQuery = "service-bus-$runId"
$catalogAProcess = $null
$catalogBProcess = $null
$requesterJob = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:scatter-gather-catalog-a$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:scatter-gather-catalog-b$' -Quiet)) {
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
            (Select-String -Path $OUTPUT_LOG -Pattern '^SUCCESS:scatter-gather-requester:received 2 replies$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern ([regex]::Escape("SUCCESS:scatter-gather-catalog-a:returned CatalogA/catalog-a-result-001 for $searchQuery")) -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern ([regex]::Escape("SUCCESS:scatter-gather-catalog-b:returned CatalogB/catalog-b-result-777 for $searchQuery")) -Quiet)) {
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

    $catalogAEnv = @{
        SC_EXAMPLES_CATALOG_A_QUEUE_NAME = $catalogAQueueName
        SC_EXAMPLES_CATALOG_B_QUEUE_NAME = $catalogBQueueName
    }

    $catalogAProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_CATALOG_A_QUEUE_NAME='$catalogAQueueName'; `$env:SC_EXAMPLES_CATALOG_B_QUEUE_NAME='$catalogBQueueName'; dotnet run --project '$catalogAProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow
    $catalogBProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_CATALOG_A_QUEUE_NAME='$catalogAQueueName'; `$env:SC_EXAMPLES_CATALOG_B_QUEUE_NAME='$catalogBQueueName'; dotnet run --project '$catalogBProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Catalog services did not become ready within 30 seconds'
    }

    $requesterJob = Start-Job -ScriptBlock {
        $env:SC_EXAMPLES_REQUESTER_QUEUE_NAME = $using:requesterQueueName
        $env:SC_EXAMPLES_CATALOG_A_QUEUE_NAME = $using:catalogAQueueName
        $env:SC_EXAMPLES_CATALOG_B_QUEUE_NAME = $using:catalogBQueueName
        $env:SC_EXAMPLES_SEARCH_QUERY = $using:searchQuery
        dotnet run --project $using:requesterProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $requesterJob | Wait-Job | Remove-Job -Force

    if (-not (Wait-ForCompletion)) {
        throw 'Scatter/gather run did not produce both catalog replies within 30 seconds'
    }
}
finally {
    if ($null -ne $requesterJob) {
        Remove-Job -Job $requesterJob -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $catalogAProcess -and -not $catalogAProcess.HasExited) {
        Stop-Process -Id $catalogAProcess.Id -Force -ErrorAction SilentlyContinue
        $catalogAProcess.WaitForExit()
    }

    if ($null -ne $catalogBProcess -and -not $catalogBProcess.HasExited) {
        Stop-Process -Id $catalogBProcess.Id -Force -ErrorAction SilentlyContinue
        $catalogBProcess.WaitForExit()
    }
}
