$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$ExamplesRoot = Split-Path -Parent $PSScriptRoot

function Start-ExampleDependencies {
    docker compose -f "$ExamplesRoot/docker-compose.yml" up -d rabbitmq mongodb
}
