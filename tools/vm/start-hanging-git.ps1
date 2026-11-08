# Guest side. Starts a git child that never finishes: `git fetch` against a local socket that accepts the
# connection and never answers. It runs in the interactive session of the user (a one-shot task), so it outlives
# the SSH call, the way a stuck git of a real user does. Stop it with stop-hanging-git.ps1.
$ErrorActionPreference = 'Stop'
$dir = 'C:\Users\claude\uu-hang'
New-Item -ItemType Directory -Force $dir | Out-Null
$port = 19418

Set-Content (Join-Path $dir 'uu-hang-listener.ps1') @"
`$l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
`$l.Start()
`$held = @()
while (`$true) { `$held += `$l.AcceptTcpClient() }
"@ -Encoding Ascii

$repo = Join-Path $dir 'repo'
if (-not (Test-Path (Join-Path $repo '.git'))) { git init -q $repo }
Set-Content (Join-Path $dir 'uu-hang-fetch.ps1') "Set-Location '$repo'; git fetch git://127.0.0.1:$port/none.git" -Encoding Ascii

$task = 'uuHangGit'
$p = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\claude" -LogonType Interactive -RunLevel Limited
foreach ($name in 'uu-hang-listener', 'uu-hang-fetch') {
  $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $dir\$name.ps1"
  Register-ScheduledTask -TaskName "$task-$name" -Action $a -Principal $p -Force | Out-Null
  Start-ScheduledTask "$task-$name"
  Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 2
Get-Process -Name git -ErrorAction SilentlyContinue | ForEach-Object { 'hanging git pid ' + $_.Id + ' started ' + $_.StartTime }
foreach ($name in 'uu-hang-listener', 'uu-hang-fetch') { Unregister-ScheduledTask "$task-$name" -Confirm:$false }
