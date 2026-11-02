[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$Fixtures,
    [string]$OutputDirectory = '',
    [switch]$AllowUnsignedPreview,
    [switch]$CleanEnvironment,
    [switch]$UninstallAfter
)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('results-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output directory already exists. Use a new directory.'}
$Installer=(Get-Item -LiteralPath $Installer).FullName
$Fixtures=(Get-Item -LiteralPath $Fixtures).FullName
$signature=Get-AuthenticodeSignature -LiteralPath $Installer
$signatureValid=$signature.Status -eq 'Valid' -and $null -ne $signature.TimeStamperCertificate
if(-not $signatureValid -and -not $AllowUnsignedPreview){throw 'A trusted timestamped installer is required. Preview testing must explicitly use -AllowUnsignedPreview.'}
$uninstallKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7BB8D838-71D7-4CA3-A7A4-DB997AFCACB9}_is1'
if(Test-Path $uninstallKey){throw 'TOPDF is already installed. Use a clean test account/PC; this tool will not replace it.'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$app=Join-Path $OutputDirectory 'installed-app'
$report=[ordered]@{schemaVersion=1;success=$false;releaseQualified=$false;cleanEnvironmentAttested=[bool]$CleanEnvironment;unsignedPreviewAllowed=[bool]$AllowUnsignedPreview;installerSha256=(Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash.ToLowerInvariant();installerSignature=$signature.Status.ToString();timestampPresent=($null -ne $signature.TimeStamperCertificate);osBuild=[Environment]::OSVersion.Version.ToString();is64BitOS=[Environment]::Is64BitOperatingSystem;globalDotnet=(Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source);globalPython=(Get-Command python.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source);installExitCode=$null;appSignatures=@();manifestVerified=$false;diagnostics=$null;uninstall=$null;error=$null}
function Save-Report { $report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'new-pc-report.json') -Encoding UTF8 }
function Run-Process([string]$Executable,[string[]]$Arguments,[int]$TimeoutSeconds=600){
    $process=Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru
    $process.Handle | Out-Null
    if(-not $process.WaitForExit($TimeoutSeconds*1000)){throw "Timed out: $Executable. Close the test window and retain logs."}
    return $process.ExitCode
}
try {
    $log=Join-Path $OutputDirectory 'install.log'
    $report.installExitCode=Run-Process $Installer @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=""',('/DIR="'+$app+'"'),('/LOG="'+$log+'"'))
    if($report.installExitCode -ne 0){throw "Installer failed: $($report.installExitCode)"}
    foreach($name in @('Topdf.exe','Topdf.dll','unins000.exe')){
        $sig=Get-AuthenticodeSignature -LiteralPath (Join-Path $app $name)
        $valid=$sig.Status -eq 'Valid' -and $null -ne $sig.TimeStamperCertificate
        $report.appSignatures+=@{file=$name;status=$sig.Status.ToString();timestampPresent=($null -ne $sig.TimeStamperCertificate);valid=$valid}
        if(-not $valid -and -not $AllowUnsignedPreview){throw "Installed artifact has no trusted timestamped signature: $name"}
    }
    $manifest=Get-Content -LiteralPath (Join-Path $app 'package-manifest.json') -Raw | ConvertFrom-Json
    foreach($file in $manifest){
        $target=[IO.Path]::GetFullPath((Join-Path $app $file.path))
        if(-not $target.StartsWith($app+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid manifest path'}
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256){throw "Installed hash mismatch: $($file.path)"}
    }
    $report.manifestVerified=$true
    $diagnosticPath=Join-Path $OutputDirectory 'app-diagnostics.json'
    $oldPath=$env:PATH; $oldPython=$env:TOPDF_PYTHON; $oldData=$env:TOPDF_DATA_DIR
    try {
        # The installed app must use its bundled engines, not the developer's PATH/Python.
        $env:PATH="$env:SystemRoot\System32;$env:SystemRoot"
        $env:TOPDF_PYTHON=$null
        $env:TOPDF_DATA_DIR=Join-Path $OutputDirectory 'app-data'
        $exit=Run-Process (Join-Path $app 'Topdf.exe') @('--verify-install',('"'+$Fixtures+'"'),('"'+$diagnosticPath+'"')) 1800
    } finally {$env:PATH=$oldPath; $env:TOPDF_PYTHON=$oldPython; $env:TOPDF_DATA_DIR=$oldData}
    $report.diagnostics=Get-Content -LiteralPath $diagnosticPath -Raw | ConvertFrom-Json
    if($exit -ne 0 -or -not $report.diagnostics.success){throw 'Conversion/preview verification failed. See app-diagnostics.json.'}
    $extensions=@($report.diagnostics.results | ForEach-Object {$_.extension.ToLowerInvariant()})
    $missing=@('.hwp','.docx','.xls','.xlsx','.dwg','.dxf','.pdf' | Where-Object {$_ -notin $extensions})
    $report['missingTestFormats']=$missing
    $report.success=$true
    $report.releaseQualified=$signatureValid -and [bool]$CleanEnvironment -and [bool]$UninstallAfter -and $missing.Count -eq 0 -and @($report.appSignatures | Where-Object {-not $_.valid}).Count -eq 0
} catch {$report.error=$_.Exception.Message}
finally {
    if($UninstallAfter -and (Test-Path -LiteralPath (Join-Path $app 'unins000.exe'))){
        try {
            $exit=Run-Process (Join-Path $app 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $OutputDirectory 'uninstall.log')+'"'))
            $report.uninstall=@{exitCode=$exit;appRemoved=(-not (Test-Path -LiteralPath (Join-Path $app 'Topdf.exe')));registrationRemoved=(-not (Test-Path $uninstallKey))}
            if($exit -ne 0 -or -not $report.uninstall.appRemoved -or -not $report.uninstall.registrationRemoved){$report.success=$false;$report.releaseQualified=$false;$report.error='Uninstall check failed'}
        } catch {$report.success=$false;$report.releaseQualified=$false;$report.error=$_.Exception.Message}
    }
    Save-Report
}
Write-Output (Join-Path $OutputDirectory 'new-pc-report.json')
if(-not $report.success){throw $report.error}
Write-Output ('PASS: installed conversion/preview; release qualified = '+$report.releaseQualified)
