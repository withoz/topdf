param([switch]$SkipNative, [switch]$SkipReam)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$vendorRoot = Join-Path $projectRoot 'src/Topdf/engines/vendor'
$downloadRoot = Join-Path $projectRoot '.local/engine-downloads'
New-Item -ItemType Directory -Force $vendorRoot, $downloadRoot | Out-Null

if (-not $SkipNative) {
    $manifest = Get-Content (Join-Path $PSScriptRoot 'engines.lock.json') -Raw | ConvertFrom-Json
    foreach ($engine in $manifest.native) {
        $archiveName = [IO.Path]::GetFileName(([uri]$engine.url).AbsolutePath)
        $archive = Join-Path $downloadRoot $archiveName
        if (-not (Test-Path -LiteralPath $archive)) {
            Write-Host "Downloading $($engine.name) $($engine.version)"
            Invoke-WebRequest $engine.url -OutFile $archive
        }
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $engine.sha256) { throw "Checksum mismatch for $archiveName. The engine was not installed." }
        $unpacked = Join-Path $downloadRoot ($engine.name + '-' + $engine.version)
        New-Item -ItemType Directory -Force $unpacked | Out-Null
        if ($engine.kind -eq 'zip') { Expand-Archive -LiteralPath $archive -DestinationPath $unpacked -Force }
        else { & tar -xf $archive -C $unpacked; if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' } }
        $name = if ($engine.destination) { $engine.destination } else { $engine.name }
        $destination = Join-Path $vendorRoot $name
        New-Item -ItemType Directory -Force $destination | Out-Null
        foreach ($relative in $engine.files) {
            Copy-Item -LiteralPath (Join-Path $unpacked $relative) -Destination $destination -Force
        }
        if ($engine.kind -eq 'tar') {
            $sourceFolder = Join-Path $destination 'source'
            New-Item -ItemType Directory -Force $sourceFolder | Out-Null
            Copy-Item -LiteralPath $archive -Destination $sourceFolder -Force
        }
    }
    # The official office2pdf release ZIP currently omits the license text.
    $officeLicense = Join-Path $vendorRoot 'office2pdf/LICENSE'
    Invoke-WebRequest 'https://raw.githubusercontent.com/developer0hye/office2pdf/v0.10.0/LICENSE' -OutFile $officeLicense
    if ((Get-FileHash -LiteralPath $officeLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'a69f60df4102c74db295b45bdeca8bb300ecaa954a319de94cbe4e8e337e98f9') { throw 'office2pdf license checksum mismatch.' }
    Write-Host 'Native engines prepared; review docs/THIRD_PARTY.md before redistribution.'
}

if (-not $SkipReam) {
    Push-Location (Join-Path $projectRoot 'tests/OpenSource/ream-build')
    try {
        & npm.cmd ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        & node build.mjs
        if ($LASTEXITCODE -ne 0) { throw 'XLS bundle build failed.' }
    }
    finally { Pop-Location }
}
Write-Host 'Engine preparation complete.'
