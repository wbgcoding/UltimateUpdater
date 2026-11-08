# Guest side. Ends the hanging git child and its listener started by start-hanging-git.ps1.
Get-Process -Name git, git-remote-https, git-remote-http -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
  Where-Object { $_.CommandLine -like '*uu-hang-*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Remove-Item -Recurse -Force 'C:\Users\claude\uu-hang' -ErrorAction SilentlyContinue
'git processes left: ' + @(Get-Process -Name git -ErrorAction SilentlyContinue).Count
