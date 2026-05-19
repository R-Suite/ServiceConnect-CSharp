$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$ExamplesRoot = Split-Path -Parent $PSScriptRoot

function Start-ExampleDependencies {
    docker compose -f "$ExamplesRoot/docker-compose.yml" up -d rabbitmq mongodb
}

# Polls until RabbitMQ accepts a TCP connection.
function Wait-Rabbit {
    param(
        [string]$Host = 'localhost',
        [int]$Port = 5672
    )
    $maxAttempts = 60
    $attempt = 0
    Write-Host "Waiting for RabbitMQ at ${Host}:${Port}..."
    while ($attempt -lt $maxAttempts) {
        try {
            $tcp = [System.Net.Sockets.TcpClient]::new()
            $tcp.Connect($Host, $Port)
            $tcp.Close()
            Write-Host 'RabbitMQ is ready.'
            return
        } catch {
            Start-Sleep -Seconds 2
            $attempt++
        }
    }
    throw "RabbitMQ at ${Host}:${Port} did not become ready within $($maxAttempts * 2) seconds."
}

# Polls until MongoDB responds to an admin ping.
function Wait-Mongo {
    param(
        [string]$Host = 'localhost',
        [int]$Port = 27017
    )
    $maxAttempts = 60
    $attempt = 0
    Write-Host "Waiting for MongoDB at ${Host}:${Port}..."
    while ($attempt -lt $maxAttempts) {
        try {
            $result = mongosh --host $Host --port $Port --quiet --eval "db.adminCommand('ping')" 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Host 'MongoDB is ready.'
                return
            }
        } catch { }
        Start-Sleep -Seconds 2
        $attempt++
    }
    throw "MongoDB at ${Host}:${Port} did not become ready within $($maxAttempts * 2) seconds."
}
