[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$CertificateThumbprint,
    [string]$SignTool = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$MachineStore,
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
if(-not $SignTool){
    $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $SignTool=Get-ChildItem $sdk -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue | Where-Object FullName -Match '\\x64\\' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if(-not $SignTool -or -not (Test-Path -LiteralPath $SignTool)){throw 'Windows SDK x64 SignTool not found.'}
$store=if($MachineStore){'LocalMachine'}else{'CurrentUser'}
$cert=Get-Item "Cert:\$store\My\$CertificateThumbprint" -ErrorAction Stop
if(-not $cert.HasPrivateKey){throw 'The code signing certificate has no accessible private key.'}
if($cert.NotBefore -gt (Get-Date) -or $cert.NotAfter -le (Get-Date)){throw 'The code signing certificate is not currently valid.'}
if($cert.Subject -eq $cert.Issuer){throw 'Self-signed certificates cannot be used for public release.'}
$eku=@($cert.Extensions | Where-Object {$_.Oid.Value -eq '2.5.29.37'} | ForEach-Object {$_.EnhancedKeyUsages} | ForEach-Object {$_.Value})
if('1.3.6.1.5.5.7.3.3' -notin $eku){throw 'Certificate has no Code Signing EKU.'}
$chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.RevocationMode='Online'
    $chain.ChainPolicy.UrlRetrievalTimeout=[TimeSpan]::FromSeconds(20)
    if(-not $chain.Build($cert)){throw ('Certificate trust check failed: '+($chain.ChainStatus.Status -join ', '))}
} finally {$chain.Dispose()}
$files=@($Path | ForEach-Object {(Get-Item -LiteralPath $_ -ErrorAction Stop).FullName})
foreach($file in $files){
    if([IO.Path]::GetExtension($file) -notin @('.exe','.dll','.msi')){throw "Unsupported signing target: $file"}
}
if($ValidateOnly){Write-Output 'PASS: trusted code signing certificate and targets validated'; return}
foreach($file in $files){
    $arguments=@('sign','/fd','SHA256','/td','SHA256','/tr',$TimestampUrl,'/s','My','/sha1',$CertificateThumbprint)
    if($MachineStore){$arguments+='/sm'}
    & $SignTool @arguments $file
    if($LASTEXITCODE -ne 0){throw "Signing failed: $file"}
    & $SignTool verify /pa /all /tw $file
    if($LASTEXITCODE -ne 0){throw "Authenticode verification failed: $file"}
    $signature=Get-AuthenticodeSignature -LiteralPath $file
    if($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate){throw "Missing valid timestamped signature: $file"}
}
