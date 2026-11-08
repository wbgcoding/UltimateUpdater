# Guest side (run through the VM driver's script verb). Seeds outdated real packages so a scan has something to find:
#   - a second per-user Python (3.11) next to the one already there, both with old pip packages (pip --user)
#   - an older Git (the current one is recorded, removed and put back by unseed-packages.ps1)
# Idempotent: every step checks its own result first. State goes to C:\Users\claude\uu-seed\state.json.
$ErrorActionPreference = 'Continue'
$state = 'C:\Users\claude\uu-seed'
$stateFile = Join-Path $state 'state.json'
New-Item -ItemType Directory -Force $state | Out-Null
$st = if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
function Save-State { $st | ConvertTo-Json | Set-Content $stateFile -Encoding Ascii }
function Prop($name, $value) { if ($null -eq $st.PSObject.Properties[$name]) { $st | Add-Member -NotePropertyName $name -NotePropertyValue $value } else { $st.$name = $value } }

$wg = @('--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
# uninstall rejects the agreement flags
$wgRemove = @('--silent', '--disable-interactivity')
$oldGit = '2.45.2'
$py311 = '3.11.9'
$pins = @('six==1.15.0', 'idna==2.10', 'colorama==0.4.3')

# Python 3.11 per user, only when it is not there yet (a pre-existing one is never removed by the unseed)
$exe311 = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python311\python.exe'
if (-not (Test-Path $exe311)) {
  'installing Python 3.11'
  winget install -e --id Python.Python.3.11 --version $py311 --scope user @wg | Out-Null
  if (Test-Path $exe311) { Prop 'installedPython311' $true }
} else { 'Python 3.11 already present' }
Save-State

# old pip packages in every per-user Python of the 3.11 and 3.12 line
foreach ($ver in '311', '312') {
  $exe = Join-Path $env:LOCALAPPDATA "Programs\Python\Python$ver\python.exe"
  if (-not (Test-Path $exe)) { "missing Python$ver"; continue }
  & $exe -m pip install --user --disable-pip-version-check --no-warn-script-location @pins 2>&1 | Select-Object -Last 2
  "Python$ver outdated:"
  & $exe -m pip list --outdated --user --format=columns --disable-pip-version-check 2>&1
}
Prop 'pipPins' $pins
Save-State

# older Git: winget refuses a downgrade, so the current version is recorded, removed and the old one installed
$gitNow = if (Get-Command git -ErrorAction SilentlyContinue) { (git --version) -replace '^git version\s+', '' } else { '' }
if ($gitNow -notlike "$oldGit*") {
  if ($null -eq $st.PSObject.Properties['gitBefore']) {
    $before = (winget list -e --id Git.Git --disable-interactivity 2>&1 | Select-String 'Git\.Git\s+(\S+)').Matches.Groups[1].Value
    Prop 'gitBefore' $before
    Save-State
  }
  'replacing Git ' + $st.gitBefore + ' with ' + $oldGit
  # a git process still running makes the uninstall fail quietly, so the result is checked and retried once
  for ($try = 1; $try -le 2; $try++) {
    Get-Process -Name git, git-remote-https, git-remote-http -ErrorAction SilentlyContinue | Stop-Process -Force
    winget uninstall -e --id Git.Git @wgRemove 2>&1 | Select-Object -Last 2
    winget install -e --id Git.Git --version $oldGit @wg 2>&1 | Select-Object -Last 2
    if ((winget list -e --id Git.Git --disable-interactivity 2>&1 | Out-String) -match [regex]::Escape($oldGit)) { break }
  }
} else { 'Git already ' + $gitNow }
Prop 'gitSeeded' $oldGit
Save-State
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
'git now: ' + (git --version)
winget list -e --id Git.Git --disable-interactivity 2>&1 | Select-Object -Last 2
'seed done'
