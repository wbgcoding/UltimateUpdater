# Guest side: removes the test package, its files and the trusted test certificate.
$ErrorActionPreference = 'Continue'
$dir = (Join-Path $env:USERPROFILE 'uu-store-test')
Get-Process -Name TestStoreApp -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dir | Out-Null
# Remove-AppxPackage needs the same interactive session as the install (SSH sessions get 0x80070005)
$inner = Join-Path $dir 'inner.ps1'; $log = Join-Path $dir 'inner.log'
Remove-Item $log -ErrorAction SilentlyContinue
Set-Content $inner "Get-AppxPackage UUTest.StoreApp | Remove-AppxPackage *> $log; 'DONE' | Add-Content $log" -Encoding Ascii
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File $inner"
$p = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\claude" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName uuStoreInner -Action $a -Principal $p -Force | Out-Null
Start-ScheduledTask uuStoreInner
for ($i = 0; $i -lt 90; $i++) { Start-Sleep 1; if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'DONE' -Quiet)) { break } }
Get-Content $log
Unregister-ScheduledTask uuStoreInner -Confirm:$false
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq 'CN=UUTestStore' } | ForEach-Object { Remove-Item $_.PSPath -Force; 'untrusted ' + $_.Thumbprint }
Remove-Item -Recurse -Force $dir
'left: ' + @(Get-AppxPackage UUTest.StoreApp).Count + ' package(s)'
