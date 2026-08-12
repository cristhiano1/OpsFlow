#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the complete OpsFlow local development environment.
.DESCRIPTION
    1. Verifies prerequisites (docker, dotnet, npm) and that .env exists.
    2. Ensures Docker Engine is running, starting Docker Desktop if needed.
    3. Starts the SQL Server container and waits for it to become healthy.
    4. Starts the ASP.NET Core backend in a new terminal (skips if already running).
    5. Starts the Vite frontend in a new terminal (skips if already running).
    6. Opens the browser at http://localhost:5175.
.PARAMETER NoBrowser
    Skip opening the default browser after all services are ready.
.EXAMPLE
    .\start-opsflow.ps1
    .\start-opsflow.ps1 -NoBrowser
#>
param(
    [switch]$NoBrowser
)

# ---------------------------------------------------------------------------
# Configuration — derived from launchSettings.json, vite.config.ts, docker-compose.yml
# ---------------------------------------------------------------------------

$repoRoot      = $PSScriptRoot
$frontendDir   = Join-Path $repoRoot 'src\frontend\opsflow-web'
$backendPort   = 5203
$frontendPort  = 5175
$backendUrl    = "http://localhost:$backendPort"
$frontendUrl   = "http://localhost:$frontendPort"
$containerName = 'opsflow-sqlserver'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function Fail([string]$Message) {
    Write-Host ""
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Fail "'$Name' was not found on PATH. Install it and ensure it is on PATH, then re-run."
    }
}

function Test-TcpPort([int]$Port) {
    $checks = @(
        @{ Addr = '127.0.0.1'; Family = [System.Net.Sockets.AddressFamily]::InterNetwork }
        @{ Addr = '::1';       Family = [System.Net.Sockets.AddressFamily]::InterNetworkV6 }
    )

    foreach ($check in $checks) {
        $tcp = $null

        try {
            $tcp = [System.Net.Sockets.TcpClient]::new($check.Family)
            $connect = $tcp.ConnectAsync($check.Addr, $Port)

            if ($connect.Wait(500) -and $tcp.Connected) {
                return $true
            }
        }
        catch {
            # Probe failed; try the next address family.
        }
        finally {
            if ($null -ne $tcp) {
                $tcp.Dispose()
            }
        }
    }

    return $false
}

function Wait-TcpPort([int]$Port, [string]$Label, [int]$TimeoutSeconds = 90) {
    Write-Host "  Waiting for $Label on port $Port ..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-TcpPort $Port) {
            Write-Host ' ready.'
            return
        }
        Start-Sleep -Seconds 2
        Write-Host '.' -NoNewline
    }
    Write-Host ''
    Fail "$Label did not become reachable on port $Port within $TimeoutSeconds seconds."
}

function Test-DockerRunning {
    # Temporarily suppress errors so stderr from docker does not surface as PS errors.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    $null = docker info 2>&1
    $ok   = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prev
    return $ok
}

function Wait-DockerEngine([int]$TimeoutSeconds = 120) {
    Write-Host '  Polling for Docker Engine to become ready' -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-DockerRunning) {
            Write-Host ' ready.'
            return
        }
        Start-Sleep -Seconds 3
        Write-Host '.' -NoNewline
    }
    Write-Host ''
    Fail "Docker Engine did not become available within $TimeoutSeconds seconds."
}

function Wait-ContainerHealthy([string]$Name, [int]$TimeoutSeconds = 120) {
    Write-Host "  Waiting for $Name to become healthy ..." -NoNewline
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $prev   = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        $status = docker inspect --format '{{.State.Health.Status}}' $Name 2>&1
        $ErrorActionPreference = $prev

        if ($status -eq 'healthy') {
            Write-Host ' healthy.'
            return
        }
        if ($status -eq 'unhealthy') {
            Write-Host ''
            Fail "Container '$Name' reported status 'unhealthy'. Inspect logs with: docker logs $Name"
        }
        Start-Sleep -Seconds 3
        Write-Host '.' -NoNewline
    }
    Write-Host ''
    Fail "Container '$Name' did not become healthy within $TimeoutSeconds seconds."
}

# ---------------------------------------------------------------------------
# Step 1 — Prerequisites
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'OpsFlow dev environment startup'
Write-Host '================================'
Write-Host ''
Write-Host '[1/6] Checking prerequisites...'

foreach ($cmd in 'docker', 'dotnet', 'npm') {
    Assert-Command $cmd
    Write-Host "  $cmd : found"
}

$envFile = Join-Path $repoRoot '.env'
if (-not (Test-Path $envFile)) {
    Fail ".env not found at '$envFile'.`nCopy .env.example to .env and fill in your local values, then re-run."
}
Write-Host "  .env  : found"

# ---------------------------------------------------------------------------
# Step 2 — Docker Engine
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '[2/6] Checking Docker Engine...'

if (Test-DockerRunning) {
    Write-Host '  Docker Engine is already running.'
} else {
    $desktopExe = 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
    if (Test-Path $desktopExe) {
        Write-Host '  Docker Engine not responding. Starting Docker Desktop...'
        Start-Process $desktopExe
        Wait-DockerEngine -TimeoutSeconds 120
    } else {
        Fail "Docker Engine is not running and Docker Desktop was not found at '$desktopExe'. Start Docker manually and re-run."
    }
}

# ---------------------------------------------------------------------------
# Step 3 — SQL Server
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '[3/6] Starting SQL Server container...'

Push-Location $repoRoot
docker compose up -d sqlserver
$composeExit = $LASTEXITCODE
Pop-Location

if ($composeExit -ne 0) {
    Fail "'docker compose up -d sqlserver' failed (exit $composeExit). See output above."
}

Wait-ContainerHealthy -Name $containerName -TimeoutSeconds 120

# ---------------------------------------------------------------------------
# Step 4 — Backend
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '[4/6] Checking backend...'

if (Test-TcpPort $backendPort) {
    Write-Host "  Backend already running on port $backendPort. Skipping launch."
} else {
    Write-Host '  Starting backend in a new terminal...'
    $backendProject = Join-Path $repoRoot 'src\backend\OpsFlow.Api'
    $backendCmd     = "dotnet run --project `"$backendProject`" --launch-profile http"
    Start-Process powershell -ArgumentList @('-NoExit', '-Command', $backendCmd) -WorkingDirectory $repoRoot
    Wait-TcpPort -Port $backendPort -Label 'backend' -TimeoutSeconds 90
}

# ---------------------------------------------------------------------------
# Step 5 — Frontend
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '[5/6] Checking frontend...'

if (Test-TcpPort $frontendPort) {
    Write-Host "  Frontend already running on port $frontendPort. Skipping launch."
} else {
    Write-Host '  Starting frontend in a new terminal...'
    $frontendCmd = "npm run dev -- --port $frontendPort --strictPort"
    Start-Process powershell -ArgumentList @('-NoExit', '-Command', $frontendCmd) -WorkingDirectory $frontendDir
    Wait-TcpPort -Port $frontendPort -Label 'frontend' -TimeoutSeconds 90
}

# ---------------------------------------------------------------------------
# Step 6 — Browser
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host '[6/6] Opening browser...'

if ($NoBrowser) {
    Write-Host '  -NoBrowser specified. Skipping.'
} else {
    Start-Process $frontendUrl
    Write-Host "  Opened $frontendUrl"
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'OpsFlow local environment is ready.'
Write-Host ''
Write-Host "  Database : healthy ($containerName)"
Write-Host "  Backend  : $backendUrl"
Write-Host "  Frontend : $frontendUrl"
Write-Host ''
