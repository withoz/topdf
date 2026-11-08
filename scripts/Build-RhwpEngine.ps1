param([string]$SourceDirectory = '', [string]$CrtDirectory = '')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$lock=Get-Content (Join-Path $PSScriptRoot 'rhwp-layout.lock.json') -Raw | ConvertFrom-Json
$patch=Join-Path $root 'src/Topdf/engines/rhwp/layout.patch'
if((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne $lock.patchSha256){throw 'rhwp layout patch checksum mismatch'}
if(-not $SourceDirectory){$SourceDirectory=Join-Path $root '.local/rhwp-layout-fix'}
if(-not (Test-Path (Join-Path $SourceDirectory '.git'))){
    & git clone --depth 1 --branch $lock.tag --filter=blob:none --sparse $lock.repository $SourceDirectory
    if($LASTEXITCODE -ne 0){throw 'rhwp source download failed'}
}
$commit=(& git -C $SourceDirectory rev-parse HEAD).Trim()
if($commit -ne $lock.commit){throw 'Unexpected rhwp source commit; use a separate source directory'}
$patterns=@('/*','!/*/','/src/','/crates/','/tools/','/bindings/','/assets/','/.cargo/','/vendor/','/saved/',
    '/mydocs/manual/agent_knowledge_map.md','/mydocs/manual/agent_troubleshooting_guide.md',
    '/mydocs/manual/recipes/','/mydocs/tech/agent_roadmap/atlas_r1_r200.md','/mydocs/manual/gym_optional_tool.md')
$patterns | & git -C $SourceDirectory sparse-checkout set --no-cone --stdin
if($LASTEXITCODE -ne 0){throw 'rhwp source preparation failed'}
& git -C $SourceDirectory apply --reverse --check $patch 2>$null
if($LASTEXITCODE -ne 0){
    & git -C $SourceDirectory diff --quiet
    if($LASTEXITCODE -ne 0){throw 'Source has other edits; refusing to overwrite them'}
    & git -C $SourceDirectory apply --check $patch
    if($LASTEXITCODE -ne 0){throw 'rhwp patch does not apply'}
    & git -C $SourceDirectory apply $patch
    if($LASTEXITCODE -ne 0){throw 'rhwp patch failed'}
}
Push-Location $SourceDirectory
try {
    & cargo build --locked --profile release-test --bin rhwp --target-dir target/pr-review
    if($LASTEXITCODE -ne 0){throw 'rhwp build failed'}
} finally {Pop-Location}
$destination=Join-Path $root 'src/Topdf/engines/vendor/rhwp'
New-Item -ItemType Directory -Force $destination | Out-Null
$binary=Join-Path $SourceDirectory 'target/pr-review/release-test/rhwp.exe'
Copy-Item -LiteralPath $binary -Destination (Join-Path $destination 'rhwp.exe') -Force
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'LICENSE'),$patch -Destination $destination -Force
if(-not $CrtDirectory){
    $runtime=Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT\vcruntime140.dll' -File -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.VersionInfo.FileVersion -replace '[^0-9.].*$','') } -Descending | Select-Object -First 1
} else {$runtime=Get-Item -LiteralPath (Join-Path $CrtDirectory 'vcruntime140.dll')}
if(-not $runtime -or (Get-AuthenticodeSignature $runtime.FullName).Status -ne 'Valid'){throw 'Provide a licensed, signed Visual Studio x64 Redistributable vcruntime140.dll using -CrtDirectory'}
Copy-Item -LiteralPath $runtime.FullName -Destination $destination -Force
'Microsoft Visual C++ application-local CRT. Original Microsoft licenses apply. See https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files and https://visualstudio.microsoft.com/license-terms/' | Set-Content (Join-Path $destination 'NOTICE-Microsoft-CRT.txt')
$metadata=[ordered]@{version=$lock.version;buildProfile='release-test';upstreamCommit=$commit;patchSha256=$lock.patchSha256;binarySha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()}
$metadata | ConvertTo-Json | Set-Content (Join-Path $destination 'topdf-layout.json') -Encoding utf8
Write-Host "Prepared $($lock.version) at $destination"
