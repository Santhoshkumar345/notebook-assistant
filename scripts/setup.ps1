<#
.SYNOPSIS
  One-time setup of the secrets the three services need. Safe to run again.

.DESCRIPTION
  The AI service and the .NET API share a service key, and both refuse to start without it. This script
    1. creates ai-service\.env from .env.example if it does not exist,
    2. puts a new random service key in it (AI_SERVICE_KEY) unless it already has one,
    3. gives the API the same key through dotnet user-secrets (stored in your user profile, not in the repository).
  The key is never printed. Your Gemini key is yours to add: it is only checked for, never touched.
#>
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$SkipUserSecrets # for testing the script itself
)

$ErrorActionPreference = 'Stop'
$envFile = Join-Path $Root 'ai-service\.env'
$example = Join-Path $Root '.env.example'
$apiProject = Join-Path $Root 'api\src\NotebookApi'
$utf8 = New-Object System.Text.UTF8Encoding($false) # no byte-order mark: dotenv reads the first variable name literally

if (-not (Test-Path $envFile)) {
    Copy-Item $example $envFile
    Write-Host 'Created ai-service\.env from .env.example.'
}

$content = [System.IO.File]::ReadAllText($envFile)
$existing = [regex]::Match($content, '(?m)^AI_SERVICE_KEY=(.*)$')
$key = if ($existing.Success) { $existing.Groups[1].Value.Trim() } else { '' }

if ($key.Length -lt 16) {
    $bytes = New-Object byte[] 24
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $generator.GetBytes($bytes)
    $generator.Dispose()
    $key = -join ($bytes | ForEach-Object { $_.ToString('x2') })

    if ($existing.Success) {
        $content = [regex]::Replace($content, '(?m)^AI_SERVICE_KEY=.*$', "AI_SERVICE_KEY=$key")
    } else {
        $content = $content.TrimEnd() + "`r`nAI_SERVICE_KEY=$key`r`n"
    }
    [System.IO.File]::WriteAllText($envFile, $content, $utf8)
    Write-Host 'Made a service key and saved it in ai-service\.env.'
} else {
    Write-Host 'ai-service\.env already has a service key: kept as it is.'
}

if ($SkipUserSecrets) {
    Write-Host 'Skipped giving the key to the API (-SkipUserSecrets).'
} else {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK (dotnet) is not installed or not on PATH.' }
    dotnet user-secrets set 'AiService:ApiKey' $key --project $apiProject | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet user-secrets failed.' }
    Write-Host 'Gave the API the same key (dotnet user-secrets).'
}

if ([regex]::IsMatch($content, '(?m)^GEMINI_API_KEY=\s*$')) {
    Write-Host ''
    Write-Host 'GEMINI_API_KEY in ai-service\.env is still empty. Questions need it; uploading documents, the tests and the end-to-end tests do not.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Start the three services (see the README, "Run it"):'
Write-Host '  1. cd ai-service ; .venv\Scripts\python -m uvicorn app.api:app_from_env --factory --port 8000'
Write-Host '  2. cd api ; dotnet run --project src/NotebookApi --urls http://127.0.0.1:5247'
Write-Host '  3. cd web ; npm install ; npm start      then open http://localhost:4200'
