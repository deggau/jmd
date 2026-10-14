[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.34',
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release',
    [string] $Makensis = 'makensis',
    [string] $WhisperRuntimeDirectory
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

if ($WhisperRuntimeDirectory) {
    $whisperCli = Join-Path $WhisperRuntimeDirectory 'whisper-cli.exe'
    if (-not (Test-Path $whisperCli)) { throw "Executável whisper-cli.exe não encontrado em: $WhisperRuntimeDirectory" }
    $whisperOutput = Join-Path $publishDir 'whisper'
    New-Item $whisperOutput -ItemType Directory -Force | Out-Null
    Copy-Item $whisperCli $whisperOutput -Force
    Get-ChildItem -Path $WhisperRuntimeDirectory -Filter '*.dll' -File | Copy-Item -Destination $whisperOutput -Force
    $whisperLicense = Join-Path $WhisperRuntimeDirectory 'LICENSE-whisper.cpp.txt'
    if (-not (Test-Path $whisperLicense)) { throw "Licença do whisper.cpp não encontrada: $whisperLicense" }
    Copy-Item $whisperLicense $whisperOutput -Force
}

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

$portugueseModel = 'vosk-model-small-pt-0.3'
$portugueseModelArchive = Join-Path $env:TEMP "$portugueseModel.zip"
$portugueseModelUrl = "https://alphacephei.com/vosk/models/$portugueseModel.zip"
$portugueseModelSha256 = '6e1ce909032e1afa7a88e68a3d628ecafff302bdf195befab308826c395e93b7'
Write-Host "Baixando modelo de transcrição em português: $portugueseModel"
Invoke-WebRequest -Uri $portugueseModelUrl -OutFile $portugueseModelArchive
try {
    $downloadedHash = (Get-FileHash -Path $portugueseModelArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $portugueseModelSha256) {
        throw "Checksum inválido para o modelo Vosk $portugueseModel."
    }
    Expand-Archive -Path $portugueseModelArchive -DestinationPath $publishDir -Force
}
finally {
    Remove-Item $portugueseModelArchive -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $publishDir "$portugueseModel\final.mdl"))) {
    throw "O modelo de português não foi extraído corretamente: $portugueseModel"
}

& $Makensis '/INPUTCHARSET' 'UTF8' "/DAPP_VERSION=$Version" "/DAPP_RUNTIME=$Runtime" "/DPUBLISH_DIR=$publishDir" "/DOUTPUT_DIR=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "makensis falhou com código $LASTEXITCODE" }

$installer = Join-Path $outputDir "JMD-$Version-$Runtime-setup.exe"
if (-not (Test-Path $installer)) { throw "Instalador não encontrado após compilação: $installer" }
Write-Host "Instalador criado: $installer"
