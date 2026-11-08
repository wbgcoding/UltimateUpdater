# Guest side. Removes the local winget test source, its listener, its https bindings and its certificate.
$ErrorActionPreference = 'Continue'
winget source remove -n uutest --disable-interactivity 2>&1 | Select-Object -Last 1
Unregister-ScheduledTask uuStoreSource -Confirm:$false -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*uu-store-source*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
netsh http delete sslcert ipport=0.0.0.0:19443 | Out-Null
netsh http delete sslcert ipport=[::]:19443 | Out-Null
netsh http delete urlacl url=https://localhost:19443/ | Out-Null
foreach ($store in 'My', 'Root') {
  Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.Subject -eq 'CN=uu-store-source' } |
    ForEach-Object { Remove-Item $_.PSPath -Force; "removed cert from $store" }
}
Remove-Item -Recurse -Force 'C:\Users\claude\uu-store-source' -ErrorAction SilentlyContinue
winget source list 2>&1 | Select-String uutest
'source stopped'
