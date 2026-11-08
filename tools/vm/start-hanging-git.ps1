# Guest side. Starts a git child that never finishes: `git fetch` against a local socket that accepts the
# connection and never answers. It runs in the interactive session of the user (a one-shot task), so it outlives
# the SSH call, the way a stuck git of a real user does. Stop it with stop-hanging-git.ps1.
$ErrorActionPreference = 'Stop'
$dir = (Join-Path $env:USERPROFILE 'uu-hang')
New-Item -ItemType Directory -Force $dir | Out-Null
$port = 19418

Set-Content (Join-Path $dir 'uu-hang-listener.ps1') @"
`$l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $port)
`$l.Start()
`$held = @()
while (`$true) { `$held += `$l.AcceptTcpClient() }
"@ -Encoding Ascii

$repo = Join-Path $dir 'repo'
# the repo is created by the fetch task itself: one made by this (administrator) session is refused by git as foreign-owned
Set-Content (Join-Path $dir 'uu-hang-fetch.ps1') "`$g = 'C:\Program Files\Git\cmd\git.exe'; if (-not (Test-Path '$repo\.git')) { & `$g init -q '$repo' }; Set-Location '$repo'; & `$g fetch git://127.0.0.1:$port/none.git *> '$dir\fetch.log'" -Encoding Ascii

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
