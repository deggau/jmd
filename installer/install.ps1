$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repository = 'deggau/jmd'
$apiUrl = "https://api.github.com/repos/$repository/releases/latest"
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'JMD-Installer' }
$installerAsset = $release.assets | Where-Object { $_.name -match '^JMD-.+-win-x64-setup\.exe$' } | Select-Object -First 1
if (-not $installerAsset) {
    throw "A release mais recente não contém um instalador JMD x64."
}
$checksumAsset = $release.assets | Where-Object { $_.name -eq "$($installerAsset.name).sha256" } | Select-Object -First 1
if (-not $checksumAsset) {
    throw "A release mais recente não contém o checksum SHA-256 do instalador."
}

$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("JMD-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
$installerPath = Join-Path $tempDir $installerAsset.name
$checksumPath = "$installerPath.sha256"

try {
    Invoke-WebRequest -Uri $installerAsset.browser_download_url -OutFile $installerPath
    Invoke-WebRequest -Uri $checksumAsset.browser_download_url -OutFile $checksumPath

    $expectedHash = ((Get-Content -Path $checksumPath -Raw).Trim() -split '\s+')[0].ToUpperInvariant()
    $actualHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'O checksum SHA-256 do instalador não confere; a instalação foi cancelada.'
    }

    Write-Host "Instalando JMD $($release.tag_name)..."
    $process = Start-Process -FilePath $installerPath -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "O instalador terminou com código $($process.ExitCode)."
    }
    Write-Host 'JMD instalado. Procure-o no menu Iniciar.'
}
finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
