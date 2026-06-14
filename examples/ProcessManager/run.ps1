$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$orchestratorProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ProcessManager.Orchestrator/ServiceConnect.Examples.ProcessManager.Orchestrator.csproj'
$inventoryProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ProcessManager.InventoryWorker/ServiceConnect.Examples.ProcessManager.InventoryWorker.csproj'
$paymentProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ProcessManager.PaymentWorker/ServiceConnect.Examples.ProcessManager.PaymentWorker.csproj'
$starterProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ProcessManager.Starter/ServiceConnect.Examples.ProcessManager.Starter.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$runId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + '-' + [Guid]::NewGuid().ToString('N')
$workflowQueueName = "process-manager-orchestrator-$runId"
$inventoryQueueName = "process-manager-inventory-$runId"
$paymentQueueName = "process-manager-payment-$runId"
$databaseName = "process_manager_$($runId.Replace('-', '_'))"
$correlationId = [Guid]::NewGuid().ToString()
$orchestratorProcess = $null
$inventoryProcess = $null
$paymentProcess = $null
$starterJob = $null

function Wait-ForReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:process-manager-orchestrator$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:inventory-worker$' -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern '^READY:payment-worker$' -Quiet)) {
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
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:process-manager-starter:submitted $correlationId$" -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:process-manager-orchestrator:started workflow $correlationId$" -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:inventory-worker:reserved inventory for $correlationId$" -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:process-manager-orchestrator:inventory reserved for $correlationId$" -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:payment-worker:captured payment for $correlationId$" -Quiet) -and
            (Select-String -Path $OUTPUT_LOG -Pattern "^SUCCESS:process-manager-orchestrator:completed workflow $correlationId$" -Quiet)) {
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

    $orchestratorProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_WORKFLOW_QUEUE_NAME='$workflowQueueName'; `$env:SC_EXAMPLES_INVENTORY_QUEUE_NAME='$inventoryQueueName'; `$env:SC_EXAMPLES_PAYMENT_QUEUE_NAME='$paymentQueueName'; `$env:SC_EXAMPLES_DATABASE_NAME='$databaseName'; dotnet run --project '$orchestratorProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow
    $inventoryProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_WORKFLOW_QUEUE_NAME='$workflowQueueName'; `$env:SC_EXAMPLES_INVENTORY_QUEUE_NAME='$inventoryQueueName'; dotnet run --project '$inventoryProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow
    $paymentProcess = Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', "`$env:SC_EXAMPLES_WORKFLOW_QUEUE_NAME='$workflowQueueName'; `$env:SC_EXAMPLES_PAYMENT_QUEUE_NAME='$paymentQueueName'; dotnet run --project '$paymentProject' 2>&1 | Out-File -FilePath '$OUTPUT_LOG' -Append") -PassThru -NoNewWindow

    if (-not (Wait-ForReady)) {
        throw 'Process manager services did not become ready within 30 seconds'
    }

    $starterJob = Start-Job -ScriptBlock {
        $env:SC_EXAMPLES_WORKFLOW_QUEUE_NAME = $using:workflowQueueName
        $env:SC_EXAMPLES_CORRELATION_ID = $using:correlationId
        dotnet run --project $using:starterProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $starterJob | Wait-Job | Remove-Job -Force
    $starterJob = $null

    if (-not (Wait-ForCompletion)) {
        throw 'Process manager workflow did not complete within 30 seconds'
    }
}
finally {
    if ($null -ne $starterJob) {
        Remove-Job -Job $starterJob -Force -ErrorAction SilentlyContinue
    }

    if ($null -ne $paymentProcess -and -not $paymentProcess.HasExited) {
        Stop-Process -Id $paymentProcess.Id -Force -ErrorAction SilentlyContinue
        $paymentProcess.WaitForExit()
    }

    if ($null -ne $inventoryProcess -and -not $inventoryProcess.HasExited) {
        Stop-Process -Id $inventoryProcess.Id -Force -ErrorAction SilentlyContinue
        $inventoryProcess.WaitForExit()
    }

    if ($null -ne $orchestratorProcess -and -not $orchestratorProcess.HasExited) {
        Stop-Process -Id $orchestratorProcess.Id -Force -ErrorAction SilentlyContinue
        $orchestratorProcess.WaitForExit()
    }
}
