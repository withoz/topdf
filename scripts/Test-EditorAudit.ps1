param([switch]$IncludeUi,[string]$ReportDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Set-Location $root
if(-not $ReportDirectory){$version=([xml](Get-Content (Join-Path $root 'src/Topdf/Topdf.csproj') -Raw)).Project.PropertyGroup.Version;$ReportDirectory="artifacts/audit-$version"}
$report=[IO.Path]::GetFullPath((Join-Path $root $ReportDirectory))
New-Item -ItemType Directory -Force $report | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $root '.local/dotnet'
$env:TOPDF_PYTHON=Join-Path $root '.local/python-embed/runtime/python.exe'
$env:PYTHONUTF8='1'
$results=[Collections.Generic.List[object]]::new()
function Invoke-AuditStep([string]$name,[string]$program,[string[]]$arguments) {
    $started=Get-Date
    & $program @arguments 2>&1 | Tee-Object -FilePath (Join-Path $report ($name+'.log')) | Out-Host
    $code=$LASTEXITCODE
    $results.Add([ordered]@{name=$name;exitCode=$code;seconds=[math]::Round(((Get-Date)-$started).TotalSeconds,2)})
    $results | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $report 'results.json') -Encoding utf8
    if($code -ne 0){throw "Audit failed: $name ($code). See $report"}
}
Invoke-AuditStep 'smart-fixtures' 'python' @('-X','utf8','tests/Smart/generate_fixtures.py')
Invoke-AuditStep 'text-replacement' $env:TOPDF_PYTHON @('-X','utf8','tests/PdfEdit/test_text_edit.py')
Invoke-AuditStep 'pdf-edit' 'python' @('-X','utf8','tests/PdfEdit/test_edit.py')
Invoke-AuditStep 'vector-drawing' 'python' @('-X','utf8','tests/PdfEdit/test_drawing.py')
Invoke-AuditStep 'typography' 'python' @('-X','utf8','tests/PdfEdit/test_typography.py')
Invoke-AuditStep 'legacy-font' $env:TOPDF_PYTHON @('-X','utf8','tests/PdfEdit/test_legacy_font.py')
Invoke-AuditStep 'release-build' 'dotnet' @('build','src/Topdf/Topdf.csproj','-c','Release','--no-restore')
Invoke-AuditStep 'export-safety' 'dotnet' @('run','--project','tests/ExportSmoke/ExportSmoke.csproj','-c','Release','--no-restore')
Invoke-AuditStep 'smart-build' 'dotnet' @('build','tests/Smart/Smart.csproj','-c','Release','--no-restore')
$harness=Join-Path $report 'ui'
Invoke-AuditStep 'ui-build' 'dotnet' @('build','tests/PreviewSmoke/PreviewSmoke.csproj','--no-restore','-o',$harness)
if($IncludeUi){
    $exe=Join-Path $harness 'PreviewSmoke.exe'
    Invoke-AuditStep 'annotation-ui' $exe @('--drawing-ui',(Join-Path $root 'artifacts/typography-test/source.pdf'))
    Invoke-AuditStep 'commands-ui' $exe @('--commands-ui',(Join-Path $root 'artifacts/typography-test/source.pdf'))
    Invoke-AuditStep 'font-views-ui' $exe @('--font-views-ui',(Join-Path $root 'artifacts/typography-test/source.pdf'))
    Invoke-AuditStep 'zoom-ui' $exe @('--zoom-ui',(Join-Path $root 'artifacts/typography-test/source.pdf'))
    Invoke-AuditStep 'unified-ui' $exe @('--unified-ui',(Join-Path $root 'artifacts/typography-test/source.pdf'))
    Invoke-AuditStep 'inline-ui' $exe @('--inline-edit-ui',(Join-Path $root 'artifacts/pdf-text-test/text-source.pdf'))
    Invoke-AuditStep 'export-ui' $exe @('--export-ui')
}
Write-Host "PASS editor audit: $($results.Count) steps. $report"
