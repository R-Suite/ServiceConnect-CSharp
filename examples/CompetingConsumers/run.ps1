$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$workerAProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.CompetingConsumers.WorkerA/ServiceConnect.Examples.CompetingConsumers.WorkerA.csproj'
$workerBProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.CompetingConsumers.WorkerB/ServiceConnect.Examples.CompetingConsumers.WorkerB.csproj'
$producerProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.CompetingConsumers.Producer/ServiceConnect.Examples.CompetingConsumers.Producer.csproj'
$OUTPUT_LOG = Join-Path $PSScriptRoot 'output.log'
$QUEUE_NAME = "competing-consumers-queue-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
$LogLock = New-Object object

$workerAProcess = $null
$workerBProcess = $null
$producerProcess = $null

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
        [string]$ProjectPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = "run --project `"$ProjectPath`""
    $startInfo.WorkingDirectory = $PSScriptRoot
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['SC_EXAMPLES_QUEUE_NAME'] = $QUEUE_NAME

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

function Test-WorkersReady {
    $lines = @(Get-OutputLines)
    return $lines.Contains('READY:worker-a') -and $lines.Contains('READY:worker-b')
}

function Test-WorkersCompleted {
    $lines = @(Get-OutputLines)
    $processedLines = @($lines | Where-Object { $_ -match '^SUCCESS:worker-(a|b):processed job-\d{3}$' })

    if ($processedLines.Count -ne 10) {
        return $false
    }

    $jobIds = @($processedLines | ForEach-Object { ($_ -split ':processed ', 2)[1] } | Sort-Object -Unique)

    return $jobIds.Count -eq 10
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
    $workerAProcess = Start-LoggedProcess $workerAProject
    $workerBProcess = Start-LoggedProcess $workerBProject

    Wait-ForCondition -Condition { Test-WorkersReady } -FailureMessage 'Workers did not become ready within 30 seconds'

    $producerProcess = Start-LoggedProcess $producerProject
    $producerProcess.Process.WaitForExit()

    if ($producerProcess.Process.ExitCode -ne 0) {
        throw "Producer exited with code $($producerProcess.Process.ExitCode)"
    }

    Wait-ForCondition -Condition { Test-WorkersCompleted } -FailureMessage 'Workers did not process all 10 jobs within 30 seconds'
}
finally {
    Stop-LoggedProcess $producerProcess
    Stop-LoggedProcess $workerAProcess
    Stop-LoggedProcess $workerBProcess
}
