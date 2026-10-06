[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.0',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Makensis = 'makensis'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/JMD.App/JMD.App.csproj'
$publishDir = Join-Path $repoRoot "artifacts/publish/$Runtime"
$outputDir = Join-Path $repoRoot 'artifacts/installer'
$installerScript = Join-Path $PSScriptRoot 'JMD.nsi'

if (-not (Get-Command $Makensis -ErrorAction SilentlyContinue)) {
    throw "NSIS (makensis) não encontrado. Instale o NSIS gratuito e adicione makensis.exe ao PATH."
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item $publishDir -ItemType Directory -Force | Out-Null
New-Item $outputDir -ItemType Directory -Force | Out-Null

& dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true `
    --output $publishDir -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou com código $LASTEXITCODE" }

$appExe = Join-Path $publishDir 'JMD.App.exe'
if (-not (Test-Path $appExe)) { throw "Executável publicado não encontrado: $appExe" }

& $Makensis "/DAPP_VERSION=$Version" "/DAPP_RUNTIME=$Runtime" "/DPUBLISH_DIR=$publishDir" "/DOUTPUT_DIR=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "makensis falhou com código $LASTEXITCODE" }

$installer = Join-Path $outputDir "JMD-$Version-$Runtime-setup.exe"
if (-not (Test-Path $installer)) { throw "Instalador não encontrado após compilação: $installer" }
Write-Host "Instalador criado: $installer"
