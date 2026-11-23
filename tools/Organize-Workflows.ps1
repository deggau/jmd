[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string] $Path = (Join-Path $env:LOCALAPPDATA 'JMD\settings.json'),
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PropertyValue($Object, [string] $Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Set-PropertyValue($Object, [string] $Name, $Value) {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

if ([string]::IsNullOrWhiteSpace($Path)) {
    throw 'Informe o caminho do settings.json com -Path.'
}
$Path = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Arquivo de configurações não encontrado: $Path"
}

$settings = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
$workflows = Get-PropertyValue $settings 'CustomShortcuts'
if ($null -eq $workflows) {
    Write-Host 'Nenhum workflow foi encontrado no arquivo.'
    return
}

$workflowRows = @()
$convertedPrograms = 0
$convertedUrls = 0
$createdLegacySteps = 0
$unclassified = 0
$changed = $false

foreach ($workflow in @($workflows)) {
    $workflowName = [string](Get-PropertyValue $workflow 'Name')
    if ([string]::IsNullOrWhiteSpace($workflowName)) { $workflowName = [string](Get-PropertyValue $workflow 'Id') }
    $targetValue = [string](Get-PropertyValue $workflow 'Target')
    $steps = @((Get-PropertyValue $workflow 'Steps') | Where-Object { $null -ne $_ })

    # Older settings stored a single launch target on the workflow itself.
    if ($steps.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($targetValue)) {
        $steps = @([pscustomobject]@{ Action = 'Open'; Value = $targetValue })
        Set-PropertyValue $workflow 'Steps' $steps
        $createdLegacySteps++
        $changed = $true
    }

    $programs = 0
    $urls = 0
    $other = 0
    foreach ($step in $steps) {
        switch ([string](Get-PropertyValue $step 'Action')) {
            'Open' {
                $target = ([string](Get-PropertyValue $step 'Value')).Trim()
                if ([string]::IsNullOrWhiteSpace($target)) {
                    $unclassified++
                }
                elseif ($target -match '^(?i:https?)://') {
                    Set-PropertyValue $step 'Action' 'OpenUrl'
                    $convertedUrls++
                    $urls++
                    $changed = $true
                }
                else {
                    Set-PropertyValue $step 'Action' 'OpenProgram'
                    $convertedPrograms++
                    $programs++
                    $changed = $true
                }
            }
            'OpenProgram' { $programs++ }
            'OpenUrl' { $urls++ }
            default { $other++ }
        }
    }
    $workflowRows += [pscustomobject]@{
        Workflow = $workflowName
        Programs = $programs
        URLs = $urls
        OtherSteps = $other
    }
}

Write-Host ''
Write-Host 'Workflows encontrados:'
$workflowRows | Format-Table -AutoSize | Out-Host
Write-Host ("Etapas antigas identificadas: {0} programa(s), {1} URL(s)." -f $convertedPrograms, $convertedUrls)
if ($createdLegacySteps -gt 0) {
    Write-Host ("Targets antigos convertidos para etapas: {0}." -f $createdLegacySteps)
}
if ($unclassified -gt 0) {
    Write-Warning ("{0} etapa(s) 'Open' sem destino foram mantidas sem alteração." -f $unclassified)
}

if (-not $Apply) {
    Write-Host "Prévia concluída; nenhum arquivo foi alterado. Execute com -Apply para organizar e criar um backup ao lado do arquivo."
    return
}
if (-not $changed) {
    Write-Host 'Não há etapas antigas para converter; o arquivo permaneceu intacto.'
    return
}

if ($PSCmdlet.ShouldProcess($Path, 'Converter etapas Open em OpenProgram/OpenUrl')) {
    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $backupPath = "$Path.bak-$timestamp"
    Copy-Item -LiteralPath $Path -Destination $backupPath
    $temporaryPath = "$Path.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $organizedJson = $settings | ConvertTo-Json -Depth 100
        [System.IO.File]::WriteAllText($temporaryPath, $organizedJson + [Environment]::NewLine,
            [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
        Write-Host "Workflows organizados. Backup: $backupPath"
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }
    }
}
