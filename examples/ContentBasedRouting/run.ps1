$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$priorityProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer/ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer.csproj'
$standardProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ContentBasedRouting.StandardConsumer/ServiceConnect.Examples.ContentBasedRouting.StandardConsumer.csproj'
$publisherProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.ContentBasedRouting.Publisher/ServiceConnect.Examples.ContentBasedRouting.Publisher.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$RunId = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$PremiumOrderId = "premium-order-$RunId"
$StandardOrderId = "standard-order-$RunId"
$PriorityQueueName = "priority-consumer-$RunId"
$StandardQueueName = "standard-consumer-$RunId"
$LogLock = New-Object object

$priorityProcess = $null
$standardProcess = $null
$publisherProcess = $null

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

function Test-ConsumersReady {
    $lines = @(Get-OutputLines)
    return $lines.Contains('READY:priority-consumer') -and $lines.Contains('READY:standard-consumer')
}

function Test-ConsumersCompleted {
    $lines = @(Get-OutputLines)
    return $lines.Contains("SUCCESS:content-based-routing-publisher:published $PremiumOrderId and $StandardOrderId") -and
        $lines.Contains("SUCCESS:priority-consumer:processed $PremiumOrderId") -and
        $lines.Contains("SUCCESS:standard-consumer:processed $StandardOrderId")
}

function Test-NoMisdirectedMessages {
    $lines = @(Get-OutputLines)
    $priorityWrong = @($lines | Where-Object { $_ -match '^SUCCESS:priority-consumer:processed standard-order-' }).Count -eq 0
    $standardWrong = @($lines | Where-Object { $_ -match '^SUCCESS:standard-consumer:processed premium-order-' }).Count -eq 0
    return $priorityWrong -and $standardWrong
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
    $consumerEnvironment = @{
        'SC_EXAMPLES_PRIORITY_QUEUE_NAME' = $PriorityQueueName
        'SC_EXAMPLES_STANDARD_QUEUE_NAME' = $StandardQueueName
    }

    $priorityProcess = Start-LoggedProcess $priorityProject $consumerEnvironment
    $standardProcess = Start-LoggedProcess $standardProject $consumerEnvironment

    Wait-ForCondition -Condition { Test-ConsumersReady } -FailureMessage 'Consumers did not become ready within 30 seconds'

    $publisherProcess = Start-LoggedProcess $publisherProject @{
        'SC_EXAMPLES_PREMIUM_ORDER_ID' = $PremiumOrderId
        'SC_EXAMPLES_STANDARD_ORDER_ID' = $StandardOrderId
    }
    $publisherProcess.Process.WaitForExit()

    if ($publisherProcess.Process.ExitCode -ne 0) {
        throw "Publisher exited with code $($publisherProcess.Process.ExitCode)"
    }

    Wait-ForCondition -Condition { Test-ConsumersCompleted } -FailureMessage 'Consumers did not process the expected routed events within 30 seconds'

    if (-not (Test-NoMisdirectedMessages)) {
        throw 'A consumer processed the wrong message type'
    }
}
finally {
    Stop-LoggedProcess $publisherProcess
    Stop-LoggedProcess $priorityProcess
    Stop-LoggedProcess $standardProcess
}
