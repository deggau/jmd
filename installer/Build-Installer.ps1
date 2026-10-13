[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.18',
    [ValidateSet('win-x64')]
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

$voiceModel = 'vosk-model-small-en-us-0.15'
$voiceModelArchive = Join-Path $env:TEMP "$voiceModel.zip"
$voiceModelUrl = "https://alphacephei.com/vosk/models/$voiceModel.zip"
$voiceModelSha256 = '30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498'
Write-Host "Baixando modelo de voz local: $voiceModel"
Invoke-WebRequest -Uri $voiceModelUrl -OutFile $voiceModelArchive
try {
    $downloadedHash = (Get-FileHash -Path $voiceModelArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $voiceModelSha256) {
        throw "Checksum inválido para o modelo Vosk $voiceModel."
    }
    Expand-Archive -Path $voiceModelArchive -DestinationPath $publishDir -Force
}
finally {
    Remove-Item $voiceModelArchive -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $publishDir "$voiceModel\am\final.mdl"))) {
    throw "O modelo de voz não foi extraído corretamente: $voiceModel"
}

& $Makensis '/INPUTCHARSET' 'UTF8' "/DAPP_VERSION=$Version" "/DAPP_RUNTIME=$Runtime" "/DPUBLISH_DIR=$publishDir" "/DOUTPUT_DIR=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "makensis falhou com código $LASTEXITCODE" }

$installer = Join-Path $outputDir "JMD-$Version-$Runtime-setup.exe"
if (-not (Test-Path $installer)) { throw "Instalador não encontrado após compilação: $installer" }
Write-Host "Instalador criado: $installer"
