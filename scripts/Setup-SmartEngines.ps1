param([string]$CrtDirectory='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$vendor=Join-Path $root 'src/Topdf/engines/vendor'
$manifest=Get-Content (Join-Path $PSScriptRoot 'smart-engines.lock.json') -Raw | ConvertFrom-Json
foreach($file in $manifest.files){
    $target=Join-Path $vendor $file.path
    New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
    if(-not (Test-Path -LiteralPath $target)) {Invoke-WebRequest $file.url -OutFile $target}
    if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256){throw "Smart engine checksum mismatch: $($file.path)"}
}
$download=Join-Path $root '.local/local-ai/llama.zip'
New-Item -ItemType Directory -Force (Split-Path -Parent $download) | Out-Null
if(-not (Test-Path -LiteralPath $download)){Invoke-WebRequest $manifest.llama.url -OutFile $download}
if((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.llama.sha256){throw 'llama.cpp archive checksum mismatch'}
Expand-Archive -LiteralPath $download -DestinationPath (Join-Path $vendor 'llama') -Force
if(-not $CrtDirectory){
    $CrtDirectory=Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\2022\*\VC\Redist\MSVC\*\x64\Microsoft.VC143.CRT' -Directory -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if(-not $CrtDirectory -or -not (Test-Path -LiteralPath (Join-Path $CrtDirectory 'msvcp140.dll'))){throw 'Provide -CrtDirectory pointing to the licensed Visual Studio x64 Redistributable CRT folder for app-local deployment.'}
foreach($target in @((Join-Path $vendor 'llama'),(Join-Path $vendor 'tesseract'))){
    foreach($dll in Get-ChildItem -LiteralPath $CrtDirectory -Filter '*.dll'){
        if((Get-AuthenticodeSignature $dll.FullName).Status -ne 'Valid'){throw 'Invalid Microsoft CRT signature'}
        Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
    }
    'Microsoft Visual C++ application-local CRT. Original Microsoft licenses apply. See https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files and https://visualstudio.microsoft.com/license-terms/' | Set-Content (Join-Path $target 'NOTICE-Microsoft-CRT.txt')
}
Write-Host 'Smart engines verified: Tesseract kor+eng, llama.cpp CPU, Qwen2.5 0.5B. No document data is uploaded.'
