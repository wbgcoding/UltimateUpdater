# Guest side, runs in the user's interactive session (started by start-store-source.ps1). A tiny winget REST source
# that offers the newer test package, so `winget upgrade` reports it and the updater shows a Store row for it.
# Reads its settings from the json file named by the first argument.
param([string]$ConfigPath)
$cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$l = New-Object System.Net.HttpListener
$l.Prefixes.Add("https://localhost:$($cfg.Port)/")
$l.Start()

$id = 'UUTest.StoreApp'
$manifest = @{ Data = @{
  PackageIdentifier = $id
  Versions = @(@{
    PackageVersion = $cfg.Version
    DefaultLocale = @{ PackageLocale = 'en-US'; Publisher = 'UU Test'; PackageName = 'UU Test Store App'; License = 'Test'; ShortDescription = 'Packaged test app' }
    Installers = @(@{
      Architecture = 'neutral'; InstallerType = 'msix'
      InstallerUrl = "https://localhost:$($cfg.Port)/package.msix"
      InstallerSha256 = $cfg.InstallerSha256; SignatureSha256 = $cfg.SignatureSha256
      PackageFamilyName = $cfg.FamilyName
    })
  })
} }
$search = @{ Data = @(@{
  PackageIdentifier = $id; PackageName = 'UU Test Store App'; Publisher = 'UU Test'
  Versions = @(@{ PackageVersion = $cfg.Version; PackageFamilyNames = @($cfg.FamilyName) })
}) }
$info = @{ Data = @{ SourceIdentifier = 'uutest'; ServerSupportedVersions = @('1.0.0') } }

function Send-Json($ctx, $obj) {
  $bytes = [Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json -Depth 12 -Compress))
  $ctx.Response.ContentType = 'application/json'
  $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
  $ctx.Response.Close()
}

while ($l.IsListening) {
  $ctx = $l.GetContext()
  $path = $ctx.Request.Url.AbsolutePath
  Add-Content (Join-Path (Split-Path $ConfigPath) 'server.log') ($ctx.Request.HttpMethod + ' ' + $path)
  try {
    if ($path -like '*/information') { Send-Json $ctx $info }
    elseif ($path -like '*/manifestSearch') {
      # only a query that names this package gets it; anything else is "no match", like a real source
      $body = (New-Object IO.StreamReader($ctx.Request.InputStream)).ReadToEnd()
      if ($body -match '(?i)uutest[._]storeapp|uu\s?test\s?store') { Send-Json $ctx $search }
      else { $ctx.Response.StatusCode = 204; $ctx.Response.Close() }
    }
    elseif ($path -like "*/packageManifests/$id") { Send-Json $ctx $manifest }
    elseif ($path -like '*/package.msix') {
      $bytes = [IO.File]::ReadAllBytes($cfg.MsixPath)
      $ctx.Response.ContentType = 'application/octet-stream'
      $ctx.Response.ContentLength64 = $bytes.Length
      $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
      $ctx.Response.Close()
    }
    else { $ctx.Response.StatusCode = 404; $ctx.Response.Close() }
  } catch { try { $ctx.Response.Abort() } catch { } }
}
