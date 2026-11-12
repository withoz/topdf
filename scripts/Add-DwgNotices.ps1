param(
    [string]$DwgDirectory = (Join-Path $PSScriptRoot '../src/Topdf/engines/vendor/libredwg'),
    [string]$Cache = (Join-Path $PSScriptRoot '../.local/engine-downloads')
)
$ErrorActionPreference = 'Stop'
$DwgDirectory = (Resolve-Path -LiteralPath $DwgDirectory).Path
$Cache = (New-Item -ItemType Directory -Force -Path $Cache).FullName
$sources = (New-Item -ItemType Directory -Force -Path (Join-Path $DwgDirectory 'source')).FullName

function Get-PinnedFile($Url, $Sha256, $Name = ([IO.Path]::GetFileName(([uri]$Url).AbsolutePath))) {
    $path = Join-Path $Cache $Name
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest $Url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $Sha256) { throw "Checksum mismatch: $Name" }
    return $path
}

$repo = 'https://repo.msys2.org/mingw'
$dependencies = @(
    @{
        Name = 'libiconv'; Version = '1.18-1'
        BinaryHash = 'FDA1CB18D83C363C73DF1A836FE9FF5C830D28B8A3A693E1C05D1FDFEA799119'
        SourceHash = 'D40DC594479C0FBA9AA492C5CBF63DD81A7CC4B91CC1D93619BB43C622D04709'
        Dlls = @('libiconv-2.dll'); Notices = @('COPYING', 'COPYING.LIB', 'README', 'libcharset/COPYING.LIB')
    },
    @{
        Name = 'pcre2'; Version = '10.47-1'
        BinaryHash = '7C9E3CD47AF02A096C0C1810D1021F63C5FB1D22DBEC91FA019D8B37EDA00D98'
        SourceHash = 'D1AF57F4346EC9542F1D191C94C470340F6268BFA732718F6DB1B89D75FD6502'
        Dlls = @('libpcre2-8-0.dll', 'libpcre2-16-0.dll'); Notices = @('COPYING', 'LICENCE.md')
    }
)
foreach ($dependency in $dependencies) {
    $name = $dependency.Name
    $version = $dependency.Version
    $binary = Get-PinnedFile "$repo/mingw64/mingw-w64-x86_64-$name-$version-any.pkg.tar.zst" $dependency.BinaryHash
    $source = Get-PinnedFile "$repo/sources/mingw-w64-$name-$version.src.tar.zst" $dependency.SourceHash
    $unpacked = (New-Item -ItemType Directory -Force -Path (Join-Path $Cache "$name-$version-binary")).FullName
    & tar -xf $binary -C $unpacked
    if ($LASTEXITCODE) { throw "Cannot extract MSYS2 $name package" }
    foreach ($dll in $dependency.Dlls) {
        $official = Join-Path $unpacked "mingw64/bin/$dll"
        $shipped = Join-Path $DwgDirectory $dll
        if ((Get-FileHash -LiteralPath $official).Hash -ne (Get-FileHash -LiteralPath $shipped).Hash) {
            throw "Shipped $dll differs from the MSYS2 binary corresponding to $source"
        }
    }
    foreach ($notice in $dependency.Notices) {
        $destination = Join-Path $DwgDirectory "licenses/$name/$notice"
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $unpacked "mingw64/share/licenses/$name/$notice") -Destination $destination -Force
    }
    Copy-Item -LiteralPath $source -Destination $sources -Force
}
$workflow = Get-PinnedFile 'https://raw.githubusercontent.com/LibreDWG/libredwg/0.14/.github/workflows/main.yml' '2BFE1180330ED0DE899070C073166EAED68E7CE6882D5390970C6D46B6065B3E' 'libredwg-0.14-windows-workflow.yml'
Copy-Item -LiteralPath $workflow -Destination $sources -Force
$duplicate = Join-Path $sources 'libredwg-source-0.14.tar.xz'
$canonical = Join-Path $sources 'libredwg-0.14.tar.xz'
if ((Test-Path -LiteralPath $duplicate) -and (Get-FileHash -LiteralPath $duplicate).Hash -eq (Get-FileHash -LiteralPath $canonical).Hash) {
    Remove-Item -LiteralPath $duplicate
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/DWG-REDISTRIBUTION.md') -Destination $DwgDirectory -Force
