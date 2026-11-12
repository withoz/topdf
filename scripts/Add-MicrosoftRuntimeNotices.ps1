param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$packageRoot = (Resolve-Path -LiteralPath $Package).Path
$dependencies = Get-Content -LiteralPath (Join-Path $packageRoot 'Topdf.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$notices = (New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'notices/Microsoft')).FullName
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/MICROSOFT-RUNTIME-NOTICE.md') -Destination $notices -Force

foreach ($library in $dependencies.libraries.Keys) {
    $name, $version = $library.Split('/', 2)
    $id = ($name -replace '^runtimepack\.', '').ToLowerInvariant()
    $files = switch ($id) {
        'microsoft.netcore.app.runtime.win-x64' { 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT' }
        'microsoft.windowsdesktop.app.runtime.win-x64' { 'LICENSE' }
        'microsoft.web.webview2' { 'LICENSE.txt', 'NOTICE.txt' }
    }
    if (!$files) { continue }
    $destination = (New-Item -ItemType Directory -Force -Path (Join-Path $notices "$id/$version")).FullName
    foreach ($file in $files) {
        Copy-Item -LiteralPath (Join-Path $nugetRoot "$id/$version/$file") -Destination $destination -Force
    }
}
