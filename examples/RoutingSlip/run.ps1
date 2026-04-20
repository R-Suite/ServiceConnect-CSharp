$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$inventoryProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RoutingSlip.InventoryStep/ServiceConnect.Examples.RoutingSlip.InventoryStep.csproj'
$billingProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RoutingSlip.BillingStep/ServiceConnect.Examples.RoutingSlip.BillingStep.csproj'
$shippingProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RoutingSlip.ShippingStep/ServiceConnect.Examples.RoutingSlip.ShippingStep.csproj'
$starterProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RoutingSlip.Starter/ServiceConnect.Examples.RoutingSlip.Starter.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$RunId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$OrderId = "routing-slip-order-$RunId"
$InventoryQueueName = "routing-slip-inventory-$RunId"
$BillingQueueName = "routing-slip-billing-$RunId"
$ShippingQueueName = "routing-slip-shipping-$RunId"
$LogLock = New-Object object

$inventoryProcess = $null
$billingProcess = $null
$shippingProcess = $null
$starterProcess = $null

function Write-LogLine {
    param([string]$Line)

    if ($null -eq $Line) {
        return
    }

    [System.Threading.Monitor]::Enter($LogLock)
    try {
        [System.IO.File]::AppendAllText($OUTPUT_LOG, $Line + [Environment]::NewLine)
    }
    finally {
        [System.Threading.Monitor]::Exit($LogLock)
    }
}

function Start-LoggedProcess {
    param(
        [string]$ProjectPath,
        [hashtable]$EnvironmentVariables
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = "run --project `"$ProjectPath`""
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($key in $EnvironmentVariables.Keys) {
        $startInfo.Environment[$key] = $EnvironmentVariables[$key]
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    $outputHandler = [System.Diagnostics.DataReceivedEventHandler] {
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) {
            Write-LogLine $eventArgs.Data
        }
    }
    $errorHandler = [System.Diagnostics.DataReceivedEventHandler] {
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) {
            Write-LogLine $eventArgs.Data
        }
    }

    $process.add_OutputDataReceived($outputHandler)
    $process.add_ErrorDataReceived($errorHandler)
    $process.Start() | Out-Null
    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()

    return [pscustomobject]@{
        Process = $process
        OutputHandler = $outputHandler
        ErrorHandler = $errorHandler
    }
}

function Stop-LoggedProcess {
    param($LoggedProcess)

    if ($null -eq $LoggedProcess) {
        return
    }

    $process = $LoggedProcess.Process
    if ($null -eq $process) {
        return
    }

    try {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }

        $process.WaitForExit()
    }
    finally {
        $process.remove_OutputDataReceived($LoggedProcess.OutputHandler)
        $process.remove_ErrorDataReceived($LoggedProcess.ErrorHandler)
        $process.Dispose()
    }
}

function Get-OutputLines {
    if (-not (Test-Path $OUTPUT_LOG)) {
        return @()
    }

    try {
        return [System.IO.File]::ReadAllLines($OUTPUT_LOG)
    }
    catch [System.IO.IOException] {
        return @()
    }
}

function Test-StepsReady {
    $lines = @(Get-OutputLines)
    return $lines.Contains('READY:inventory-step') -and
        $lines.Contains('READY:billing-step') -and
        $lines.Contains('READY:shipping-step')
}

function Test-StepsCompleted {
    $lines = @(Get-OutputLines)
    return $lines.Contains("SUCCESS:routing-slip-starter:routed $OrderId") -and
        $lines.Contains("SUCCESS:inventory-step:processed $OrderId at InventoryStep") -and
        $lines.Contains("SUCCESS:billing-step:processed $OrderId at BillingStep") -and
        $lines.Contains("SUCCESS:shipping-step:processed $OrderId at ShippingStep")
}

function Wait-ForCondition {
    param(
        [scriptblock]$Condition,
        [string]$FailureMessage
    )

    $maxAttempts = 60

    for ($attempt = 0; $attempt -lt $maxAttempts; $attempt++) {
        if (& $Condition) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw $FailureMessage
}

try {
    Start-ExampleDependencies

    '' | Set-Content -Path $OUTPUT_LOG
    $sharedEnvironment = @{
        'SC_EXAMPLES_INVENTORY_QUEUE_NAME' = $InventoryQueueName
        'SC_EXAMPLES_BILLING_QUEUE_NAME' = $BillingQueueName
        'SC_EXAMPLES_SHIPPING_QUEUE_NAME' = $ShippingQueueName
    }

    $inventoryProcess = Start-LoggedProcess $inventoryProject $sharedEnvironment
    $billingProcess = Start-LoggedProcess $billingProject $sharedEnvironment
    $shippingProcess = Start-LoggedProcess $shippingProject $sharedEnvironment

    Wait-ForCondition -Condition { Test-StepsReady } -FailureMessage 'Step consumers did not become ready within 30 seconds'

    $starterEnvironment = @{}
    foreach ($key in $sharedEnvironment.Keys) {
        $starterEnvironment[$key] = $sharedEnvironment[$key]
    }
    $starterEnvironment['SC_EXAMPLES_ORDER_ID'] = $OrderId

    $starterProcess = Start-LoggedProcess $starterProject $starterEnvironment
    $starterProcess.Process.WaitForExit()

    if ($starterProcess.Process.ExitCode -ne 0) {
        throw "Starter exited with code $($starterProcess.Process.ExitCode)"
    }

    Wait-ForCondition -Condition { Test-StepsCompleted } -FailureMessage 'Routing slip did not complete all three steps within 30 seconds'
}
finally {
    Stop-LoggedProcess $starterProcess
    Stop-LoggedProcess $inventoryProcess
    Stop-LoggedProcess $billingProcess
    Stop-LoggedProcess $shippingProcess
}
