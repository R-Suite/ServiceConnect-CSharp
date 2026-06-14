$ErrorActionPreference = 'Stop'
Set-Location -Path (Split-Path -Parent $MyInvocation.MyCommand.Path)
. "..\scripts\common.ps1"

$Mode = $env:MODE; if (-not $Mode) { $Mode = 'smoke' }
$Duration = $env:DURATION; if (-not $Duration) { $Duration = '5m' }
$Rate = $env:RATE; if (-not $Rate) { $Rate = '100' }
$Persistence = $env:PERSISTENCE; if (-not $Persistence) { $Persistence = 'inmemory' }

try {
  docker compose -p stress-harness up -d
  Wait-Rabbit -Host 'localhost' -Port 5672
  if ($Persistence -eq 'mongo') { Wait-Mongo -Host 'localhost' -Port 27017 }
  dotnet run --project src\ServiceConnect.Examples.StressHarness\ServiceConnect.Examples.StressHarness.csproj -- `
    --mode $Mode `
    --duration $Duration `
    --rate $Rate `
    --persistence $Persistence
} finally {
  docker compose -p stress-harness down --remove-orphans | Out-Null
}
