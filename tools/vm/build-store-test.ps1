# Host side. Builds two signed MSIX versions of a tiny packaged WinForms app for the VM Store tests.
# The exe has its own icon (dark shape on white) while the package logo is a white glyph on the manifest
# color, the same split many real Store apps have. The private key lives only in the host's
# CurrentUser store and is deleted again once both versions are signed; only the .cer leaves the host.
#   powershell -NoProfile -File tools\vm\build-store-test.ps1 [-OutDir <dir>] [-Versions 1.0.0.0,2.0.0.0]
param(
  [string]$OutDir = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.tmp\store-test'),
  [string[]]$Versions = @('1.0.0.0', '2.0.0.0'),
  [string]$Kits = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$subject = 'CN=UUTestStore'
$makeappx = Join-Path $Kits 'makeappx.exe'
$signtool = Join-Path $Kits 'signtool.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($t in $makeappx, $signtool, $csc) { if (-not (Test-Path $t)) { throw "missing tool: $t" } }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function New-Glyph([int]$size, [System.Drawing.Color]$shape, [System.Drawing.Color]$back) {
  # a ring with a dot in the middle and a gap at the upper right: a knot-like mark, not any real brand
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear($back)
  $pen = New-Object System.Drawing.Pen $shape, ([single]($size * 0.13))
  $m = $size * 0.2
  $g.DrawArc($pen, [single]$m, [single]$m, [single]($size - 2 * $m), [single]($size - 2 * $m), 300, 300)
  $brush = New-Object System.Drawing.SolidBrush $shape
  $d = $size * 0.2
  $g.FillEllipse($brush, [single](($size - $d) / 2), [single](($size - $d) / 2), [single]$d, [single]$d)
  $g.Dispose(); $pen.Dispose(); $brush.Dispose()
  return $bmp
}

function Save-Png($bmp, [string]$path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

function Save-Ico([string]$path, [int[]]$sizes) {
  $frames = foreach ($s in $sizes) {
    $bmp = New-Glyph $s ([System.Drawing.Color]::FromArgb(24, 24, 31)) ([System.Drawing.Color]::White)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
  }
  $fs = [System.IO.File]::Create($path)
  $w = New-Object System.IO.BinaryWriter $fs
  $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
  }
  foreach ($f in $frames) { $w.Write($f) }
  $w.Close(); $fs.Close()
}

# Find or create the signing certificate (CurrentUser store on the host only).
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
  $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'UU store test' `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(2) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir 'UUTestStore.cer') -Force | Out-Null

try {
  foreach ($ver in $Versions) {
    $stage = Join-Path $OutDir "stage-$ver"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force (Join-Path $stage 'app'), (Join-Path $stage 'Assets') | Out-Null

    Save-Ico (Join-Path $OutDir 'TestStoreApp.ico') @(16, 32, 48, 256)
    $verSrc = Join-Path $OutDir "AssemblyVersion-$ver.cs"
    Set-Content -Path $verSrc -Value "[assembly: System.Reflection.AssemblyVersion(`"$ver`")]" -Encoding Ascii
    & $csc /nologo /target:winexe /platform:anycpu "/win32icon:$(Join-Path $OutDir 'TestStoreApp.ico')" `
      "/out:$(Join-Path $stage 'app\TestStoreApp.exe')" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'TestStoreApp.cs') $verSrc
    if ($LASTEXITCODE -ne 0) { throw 'csc failed' }

    # package logos: a white glyph on transparent, the manifest color shows behind it
    $white = [System.Drawing.Color]::White
    $clear = [System.Drawing.Color]::Transparent
    Save-Png (New-Glyph 44 $white $clear) (Join-Path $stage 'Assets\Square44x44Logo.png')
    Save-Png (New-Glyph 150 $white $clear) (Join-Path $stage 'Assets\Square150x150Logo.png')
    Save-Png (New-Glyph 50 $white $clear) (Join-Path $stage 'Assets\StoreLogo.png')

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="UUTest.StoreApp" Publisher="$subject" Version="$ver" ProcessorArchitecture="neutral" />
  <Properties>
    <DisplayName>UU Test Store App</DisplayName>
    <PublisherDisplayName>UU Test</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources><Resource Language="en-us" /></Resources>
  <Applications>
    <Application Id="App" Executable="app\TestStoreApp.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="UU Test Store App" Description="Packaged test app"
          Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png"
          BackgroundColor="#3143FF" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
    [System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

    $msix = Join-Path $OutDir "UUTestStore-$ver.msix"
    if (Test-Path $msix) { Remove-Item -Force $msix }
    & $makeappx pack /o /d $stage /p $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
    Remove-Item -Recurse -Force $stage

    # one self-contained guest script per version: `vmctl script` is the only channel that needs no file copy
    $b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($msix))
    $cerB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $OutDir 'UUTestStore.cer')))
    # Add-AppxPackage fails with 0x80070005 (process lifetime manager) from an SSH session, so the package step
    # runs in a one-shot interactive task of the same user, like a double-click would
    $guest = @"
# Guest side: trust the test certificate (LocalMachine only) and install or update the test package to $ver.
`$ErrorActionPreference = 'Stop'
`$dir = Join-Path `$env:USERPROFILE 'uu-store-test'
New-Item -ItemType Directory -Force `$dir | Out-Null
`$cer = Join-Path `$dir 'UUTestStore.cer'
`$msix = Join-Path `$dir 'UUTestStore-$ver.msix'
[IO.File]::WriteAllBytes(`$cer, [Convert]::FromBase64String('$cerB64'))
[IO.File]::WriteAllBytes(`$msix, [Convert]::FromBase64String('$b64'))
Import-Certificate -FilePath `$cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
`$inner = Join-Path `$dir 'inner.ps1'; `$log = Join-Path `$dir 'inner.log'
Remove-Item `$log -ErrorAction SilentlyContinue
Set-Content `$inner ("Add-AppxPackage -Path `$msix *> `$log; 'DONE' | Add-Content `$log") -Encoding Ascii
`$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `$inner"
`$p = New-ScheduledTaskPrincipal -UserId "`$env:COMPUTERNAME\`$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName uuStoreInner -Action `$a -Principal `$p -Force | Out-Null
Start-ScheduledTask uuStoreInner
for (`$i = 0; `$i -lt 90; `$i++) { Start-Sleep 1; if ((Test-Path `$log) -and (Select-String -Path `$log -Pattern 'DONE' -Quiet)) { break } }
Get-Content `$log
Unregister-ScheduledTask uuStoreInner -Confirm:`$false
Get-AppxPackage UUTest.StoreApp | ForEach-Object { 'installed ' + `$_.PackageFullName + ' at ' + `$_.InstallLocation }
"@
    Set-Content -Path (Join-Path $OutDir "install-$ver.ps1") -Value $guest -Encoding Ascii
    "built $msix"
  }
}
finally {
  # the packages are signed; the private key has no further use on the host
  Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
}

# A winget REST source that offers the newest version, so `winget upgrade` (and with it the updater) sees an update
# for the installed older one. The generated guest script carries the server, the package and its hashes.
$newest = $Versions[-1]
$newestMsix = Join-Path $OutDir "UUTestStore-$newest.msix"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($newestMsix)
try {
  $entry = $zip.GetEntry('AppxSignature.p7x')
  $sigStream = $entry.Open(); $ms = New-Object System.IO.MemoryStream; $sigStream.CopyTo($ms); $sigStream.Dispose()
  $sigHash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ms.ToArray())).Replace('-', '')
} finally { $zip.Dispose() }
$msixHash = (Get-FileHash $newestMsix -Algorithm SHA256).Hash
$serverB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'store-source-server.ps1')))
$msixB64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($newestMsix))
$startScript = @"
# Guest side: serve version $newest of the test package through a local winget REST source named uutest.
`$ErrorActionPreference = 'Stop'
`$dir = Join-Path `$env:USERPROFILE 'uu-store-source'
New-Item -ItemType Directory -Force `$dir | Out-Null
[IO.File]::WriteAllBytes((Join-Path `$dir 'server.ps1'), [Convert]::FromBase64String('$serverB64'))
[IO.File]::WriteAllBytes((Join-Path `$dir 'package.msix'), [Convert]::FromBase64String('$msixB64'))
`$cfg = @{ Port = 19443; Version = '$newest'; InstallerSha256 = '$msixHash'; SignatureSha256 = '$sigHash'
  FamilyName = 'UUTest.StoreApp_ehz6a4kcz691e'; MsixPath = (Join-Path `$dir 'package.msix') }
`$cfg | ConvertTo-Json | Set-Content (Join-Path `$dir 'config.json') -Encoding Ascii
# winget accepts a REST source only over https: a throw-away localhost certificate, trusted on this VM only
`$cert = New-SelfSignedCertificate -DnsName localhost -Subject 'CN=uu-store-source' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(1)
`$rootCer = Join-Path `$dir 'localhost.cer'
Export-Certificate -Cert `$cert -FilePath `$rootCer | Out-Null
Import-Certificate -FilePath `$rootCer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
netsh http add urlacl url=https://localhost:19443/ user=`$env:USERNAME | Out-Null
netsh http add sslcert ipport=0.0.0.0:19443 certhash=`$(`$cert.Thumbprint) appid='{5b6b8d3a-4c1f-4f0e-9a39-6f0d6a7f1e11}' | Out-Null
netsh http add sslcert ipport=[::]:19443 certhash=`$(`$cert.Thumbprint) appid='{5b6b8d3a-4c1f-4f0e-9a39-6f0d6a7f1e12}' | Out-Null
`$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `$dir\server.ps1 `$dir\config.json"
`$p = New-ScheduledTaskPrincipal -UserId "`$env:COMPUTERNAME\`$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName uuStoreSource -Action `$a -Principal `$p -Force | Out-Null
Start-ScheduledTask uuStoreSource
Start-Sleep -Seconds 4
winget source add -n uutest -a https://localhost:19443/api -t Microsoft.Rest --accept-source-agreements --disable-interactivity
winget upgrade --include-unknown --disable-interactivity --accept-source-agreements
"@
Set-Content -Path (Join-Path $OutDir 'start-store-source.ps1') -Value $startScript -Encoding Ascii
"certificate (public part only): $(Join-Path $OutDir 'UUTestStore.cer')"
