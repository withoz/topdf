param([string]$Output = '')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $Output){$Output=Join-Path $root '.local/python-embed/runtime'}
if(Test-Path -LiteralPath $Output){throw "Output already exists: $Output. Use a new destination."}
$downloads=Join-Path $root '.local/python-embed'
New-Item -ItemType Directory -Force $downloads | Out-Null
$archive=Join-Path $downloads 'python.zip'
if(-not (Test-Path -LiteralPath $archive)){
    Invoke-WebRequest 'https://www.python.org/ftp/python/3.14.3/python-3.14.3-embed-amd64.zip' -OutFile $archive
}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($hash -ne 'ad4961a479dedbeb7c7d113253f8db1b1935586b73c27488712beec4f2c894e6'){throw 'Python archive checksum mismatch'}
New-Item -ItemType Directory -Force $Output | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $Output
if((Get-AuthenticodeSignature (Join-Path $Output 'python.exe')).Status -ne 'Valid'){throw 'Invalid Python signature'}
@'
python314.zip
.
Lib/site-packages
import site
'@ | Set-Content (Join-Path $Output 'python314._pth') -Encoding ascii
& python -m pip install --no-deps --target (Join-Path $Output 'Lib/site-packages') -r (Join-Path $PSScriptRoot 'python-runtime-requirements.lock.txt') --disable-pip-version-check
if($LASTEXITCODE -ne 0){throw 'Python package preparation failed'}
& (Join-Path $Output 'python.exe') -c "import ezdxf, matplotlib, pypdf, win32com.client; print('Embedded runtime ready')"
if($LASTEXITCODE -ne 0){throw 'Python runtime import check failed'}
