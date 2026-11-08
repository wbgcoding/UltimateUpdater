# Guest side. Undoes seed-packages.ps1: stops the hanging git child, removes the seeded pip packages, removes a
# Python 3.11 the seed installed, and puts the recorded Git version back. Safe to run twice.
$ErrorActionPreference = 'Continue'
$state = (Join-Path $env:USERPROFILE 'uu-seed')
$stateFile = Join-Path $state 'state.json'
$wg = @('--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
# uninstall rejects the agreement flags
$wgRemove = @('--silent', '--disable-interactivity')
if (-not (Test-Path $stateFile)) { 'no seed state, nothing to undo'; return }
$st = Get-Content $stateFile -Raw | ConvertFrom-Json

# a stuck git child is the first thing that would block a Git uninstall
Get-Process -Name git, git-remote-https, git-remote-http -ErrorAction SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*uu-hang-*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if ($st.pipPins) {
  $names = @($st.pipPins | ForEach-Object { ($_ -split '==')[0] })
  foreach ($ver in '311', '312') {
    $exe = Join-Path $env:LOCALAPPDATA "Programs\Python\Python$ver\python.exe"
    if (Test-Path $exe) { & $exe -m pip uninstall -y --disable-pip-version-check @names 2>&1 | Select-Object -Last 1 }
  }
}
if ($st.installedPython311) {
  winget uninstall -e --id Python.Python.3.11 @wgRemove | Out-Null
  'Python 3.11 removed'
}
if ($st.gitSeeded) {
  winget uninstall -e --id Git.Git @wgRemove | Out-Null
  if ($st.gitBefore) { winget install -e --id Git.Git --version $st.gitBefore @wg | Out-Null } else { winget install -e --id Git.Git @wg | Out-Null }
  'Git restored: ' + (winget list -e --id Git.Git --disable-interactivity 2>&1 | Select-Object -Last 1)
}
Remove-Item -Recurse -Force $state
Remove-Item -Recurse -Force (Join-Path $env:USERPROFILE 'uu-hang') -ErrorAction SilentlyContinue
'unseed done'
