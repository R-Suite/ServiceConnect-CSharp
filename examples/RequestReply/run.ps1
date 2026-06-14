$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. "$PSScriptRoot/../scripts/common.ps1"

$responderProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RequestReply.Responder/ServiceConnect.Examples.RequestReply.Responder.csproj'
$requesterProject = Join-Path $PSScriptRoot 'src/ServiceConnect.Examples.RequestReply.Requester/ServiceConnect.Examples.RequestReply.Requester.csproj'
$responderProcess = $null
$requesterJob = $null

function Wait-ForResponderReady {
    $timeout = 30
    $elapsed = 0

    while ($elapsed -lt $timeout) {
        if ((Test-Path $OUTPUT_LOG) -and (Select-String -Path $OUTPUT_LOG -Pattern "READY:request-reply-responder" -Quiet)) {
            return $true
        }

        Start-Sleep -Milliseconds 500
        $elapsed += 0.5
    }

    return $false
}

$OUTPUT_LOG = Join-Path $PSScriptRoot "output.log"

try {
    Start-ExampleDependencies
    "" | Set-Content -Path $OUTPUT_LOG
    $responderProcess = Start-Process dotnet -ArgumentList @('run', '--project', $responderProject) -PassThru -NoNewWindow -RedirectStandardOutput $OUTPUT_LOG -RedirectStandardError $OUTPUT_LOG

    if (-not (Wait-ForResponderReady)) {
        throw "Responder did not become ready within 30 seconds"
    }

    $requesterJob = Start-Job -ScriptBlock {
        dotnet run --project $using:requesterProject 2>&1 | Out-File -FilePath $using:OUTPUT_LOG -Append
    }

    $requesterJob | Wait-Job | Remove-Job -Force
}
finally {
    if ($null -ne $responderProcess -and -not $responderProcess.HasExited) {
        Stop-Process -Id $responderProcess.Id -Force -ErrorAction SilentlyContinue
        $responderProcess.WaitForExit()
    }
}