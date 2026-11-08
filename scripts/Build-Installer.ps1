param(
    [string]$Compiler = '',
    [string]$PythonRuntime = '',
    [string]$Bootstrapper = '',
    [string]$CertificateThumbprint = '',
    [string]$SignTool = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$MachineStore
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$version=([xml](Get-Content (Join-Path $root 'src/Topdf/Topdf.csproj') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if(-not $Compiler){$Compiler=Join-Path $root '.local/inno/ISCC.exe'}
if(-not $PythonRuntime){$PythonRuntime=Join-Path $root '.local/python-embed/runtime'}
if(-not $Bootstrapper){$Bootstrapper=Join-Path $root '.local/inno-download/MicrosoftEdgeWebview2Setup.exe'}
foreach($file in @($Compiler,(Join-Path $PythonRuntime 'python.exe'),$Bootstrapper)){
    if(-not (Test-Path -LiteralPath $file)){throw "Missing build prerequisite: $file. See docs/INSTALLER.md"}
}
if((Get-AuthenticodeSignature $Bootstrapper).Status -ne 'Valid'){throw 'Invalid WebView2 bootstrapper signature'}
foreach($relative in @('rhwp/rhwp.exe','office2pdf/office2pdf.exe','libredwg/dwg2dxf.exe','libredwg/COPYING','ream/index.html','ream/ream.bundle.mjs','tesseract/tesseract50.dll','tesseract/tessdata/kor.traineddata','tesseract/msvcp140.dll','llama/llama-server.exe','llama/models/qwen2.5-0.5b-instruct-q4_k_m.gguf')){
    if(-not (Test-Path -LiteralPath (Join-Path $root "src/Topdf/engines/vendor/$relative"))){throw "Missing conversion engine: $relative. Run scripts/Setup-Engines.ps1 first."}
}
$layoutMetadata=Join-Path $root 'src/Topdf/engines/vendor/rhwp/topdf-layout.json'
if(-not (Test-Path -LiteralPath $layoutMetadata)){throw 'Missing patched HWP engine. Run scripts/Build-RhwpEngine.ps1.'}
$layout=Get-Content -LiteralPath $layoutMetadata -Raw | ConvertFrom-Json
$layoutLock=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rhwp-layout.lock.json') -Raw | ConvertFrom-Json
if($layout.patchSha256 -ne $layoutLock.patchSha256 -or $layout.binarySha256 -ne (Get-FileHash -LiteralPath (Join-Path $root 'src/Topdf/engines/vendor/rhwp/rhwp.exe') -Algorithm SHA256).Hash.ToLowerInvariant()){throw 'Patched HWP engine does not match pinned source/patch. Rebuild it.'}
if(-not (Test-Path -LiteralPath (Join-Path $root 'src/Topdf/engines/vendor/rhwp/vcruntime140.dll'))){throw 'Missing app-local rhwp runtime'}
$stage=Join-Path $root ('artifacts/installer-build-'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $stage 'package'
New-Item -ItemType Directory -Force $package | Out-Null
& dotnet publish (Join-Path $root 'src/Topdf/Topdf.csproj') -c Release -r win-x64 --self-contained true -o $package
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
if($CertificateThumbprint){
    if(-not $SignTool){
        $SignTool=Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin') -Filter signtool.exe -Recurse | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    & (Join-Path $PSScriptRoot 'Sign-Artifact.ps1') -Path (Join-Path $package 'Topdf.exe'),(Join-Path $package 'Topdf.dll') -CertificateThumbprint $CertificateThumbprint -SignTool $SignTool -TimestampUrl $TimestampUrl -MachineStore:$MachineStore
}
$runtime=Join-Path $package 'runtime/python'
New-Item -ItemType Directory -Force $runtime | Out-Null
Copy-Item -Path (Join-Path $PythonRuntime '*') -Destination $runtime -Recurse -Force
& (Join-Path $runtime 'python.exe') -c "import ezdxf, matplotlib, pypdf, win32com.client, pypdfium2, xlrd, reportlab; print('CAD and smart document runtime verified')"
if($LASTEXITCODE -ne 0){throw 'Incomplete Python runtime'}
$notices=Join-Path $package 'notices'
New-Item -ItemType Directory -Force $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'docs/THIRD_PARTY.md') -Destination $notices
# Copy only files admitted by .gitignore; no customer documents or output/cache.
$source=Join-Path $stage 'source'
New-Item -ItemType Directory -Force $source | Out-Null
Push-Location $root
try {
    $files=@(& git ls-files --cached --others --exclude-standard)
    foreach($relative in $files){
        $from=Join-Path $root $relative
        if(-not (Test-Path -LiteralPath $from -PathType Leaf)){continue}
        $to=Join-Path $source $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to
    }
} finally {Pop-Location}
Compress-Archive -Path (Join-Path $source '*') -DestinationPath (Join-Path $notices 'topdf-source.zip')
$manifest=Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($package,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'package-manifest.json') -Encoding utf8
$output=Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory -Force $output | Out-Null
$compilerArguments=@('/Q',"/DPackageDir=$package","/DOutputDir=$output","/DBootstrapper=$Bootstrapper","/DAppVersion=$version")
if($CertificateThumbprint){
    $machineOption=if($MachineStore){' /sm'}else{''}
    $command='"'+$SignTool+'" sign /fd SHA256 /td SHA256 /tr "'+$TimestampUrl+'" /s My /sha1 '+$CertificateThumbprint+$machineOption+' $f'
    $compilerArguments+=@('/DReleaseSigned=1',('/Stopdf='+$command))
}
& $Compiler @compilerArguments (Join-Path $root 'installer/Topdf.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
if($CertificateThumbprint){
    $installer=Join-Path $output "TOPDF-Setup-$version-win-x64.exe"
    & $SignTool verify /pa /all /tw $installer
    if($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature $installer).Status -ne 'Valid' -or -not (Get-AuthenticodeSignature $installer).TimeStamperCertificate){throw 'Signed installer verification failed'}
}
Write-Host "Package: $package"
Get-ChildItem $output -Filter '*.exe' | ForEach-Object {Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256}
