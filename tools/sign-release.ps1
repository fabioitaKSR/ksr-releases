#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Initialize,
    [string]$Path,
    [string]$KeyDirectory = (Join-Path $env:LOCALAPPDATA 'KSR\ReleaseSigning')
)
$ErrorActionPreference='Stop'
$keyFile=Join-Path $KeyDirectory 'release-key.dpapi'
$publicFile=Join-Path $KeyDirectory 'release-public.spki'
$entropy=[Text.Encoding]::UTF8.GetBytes('KSR release signing v1')
if ($Initialize) {
    if (Test-Path -LiteralPath $keyFile) { throw 'A signing key already exists; refusing to replace it.' }
    New-Item -ItemType Directory -Force -Path $KeyDirectory | Out-Null
    $acl=Get-Acl -LiteralPath $KeyDirectory
    $acl.SetAccessRuleProtection($true,$false)
    foreach($identity in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $KeyDirectory -AclObject $acl
    $rsa=[Security.Cryptography.RSA]::Create(3072)
    $raw=$rsa.ExportPkcs8PrivateKey()
    try {
        $protected=[Security.Cryptography.ProtectedData]::Protect($raw,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes($keyFile,$protected)
        [IO.File]::WriteAllBytes($publicFile,$rsa.ExportSubjectPublicKeyInfo())
    } finally { [Array]::Clear($raw,0,$raw.Length);$rsa.Dispose() }
    Write-Host 'Signing key created in the protected local Windows user store. Back up separately before relying on it for recovery.'
}
if ($Path) {
    $raw=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $rsa=[Security.Cryptography.RSA]::Create()
    try {
        $read=0;$rsa.ImportPkcs8PrivateKey($raw,[ref]$read)
        $bytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
        $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        $public=$rsa.ExportSubjectPublicKeyInfo()
        $keyId=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($public)).ToLowerInvariant()
        $envelope=@{algorithm='RSA-PSS-SHA256';keyId=$keyId;signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json -Compress
        [IO.File]::WriteAllText(([IO.Path]::GetFullPath($Path)+'.sig'),$envelope,[Text.UTF8Encoding]::new($false))
        Write-Host ('Signed: '+[IO.Path]::GetFileName($Path))
    } finally { [Array]::Clear($raw,0,$raw.Length);$rsa.Dispose() }
}
if (Test-Path -LiteralPath $publicFile) {
    [pscustomobject]@{PublicKeyBase64=[Convert]::ToBase64String([IO.File]::ReadAllBytes($publicFile));KeyDirectory=$KeyDirectory} | ConvertTo-Json -Compress
}
