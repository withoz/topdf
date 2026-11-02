param(
    [string]$Compiler = '',
    [string]$PythonRuntime = '',
    [string]$Bootstrapper = ''
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $Compiler){$Compiler=Join-Path $root '.local/inno/ISCC.exe'}
if(-not $PythonRuntime){$PythonRuntime=Join-Path $root '.local/python-embed/runtime'}
if(-not $Bootstrapper){$Bootstrapper=Join-Path $root '.local/inno-download/MicrosoftEdgeWebview2Setup.exe'}
foreach($file in @($Compiler,(Join-Path $PythonRuntime 'python.exe'),$Bootstrapper)){
    if(-not (Test-Path -LiteralPath $file)){throw "Missing build prerequisite: $file. See docs/INSTALLER.md"}
}
if((Get-AuthenticodeSignature $Bootstrapper).Status -ne 'Valid'){throw 'Invalid WebView2 bootstrapper signature'}
foreach($relative in @('rhwp/rhwp.exe','office2pdf/office2pdf.exe','libredwg/dwg2dxf.exe','libredwg/COPYING','ream/index.html','ream/ream.bundle.mjs')){
    if(-not (Test-Path -LiteralPath (Join-Path $root "src/Topdf/engines/vendor/$relative"))){throw "Missing conversion engine: $relative. Run scripts/Setup-Engines.ps1 first."}
}
$stage=Join-Path $root ('artifacts/installer-build-'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $stage 'package'
New-Item -ItemType Directory -Force $package | Out-Null
& dotnet publish (Join-Path $root 'src/Topdf/Topdf.csproj') -c Release -r win-x64 --self-contained true -o $package
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
$runtime=Join-Path $package 'runtime/python'
New-Item -ItemType Directory -Force $runtime | Out-Null
Copy-Item -Path (Join-Path $PythonRuntime '*') -Destination $runtime -Recurse -Force
& (Join-Path $runtime 'python.exe') -c "import ezdxf, matplotlib, pypdf, win32com.client; print('CAD runtime verified')"
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
& $Compiler '/Q' "/DPackageDir=$package" "/DOutputDir=$output" "/DBootstrapper=$Bootstrapper" (Join-Path $root 'installer/Topdf.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
Write-Host "Package: $package"
Get-ChildItem $output -Filter '*.exe' | ForEach-Object {Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256}
