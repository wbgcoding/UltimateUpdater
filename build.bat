@echo off
setlocal enabledelayedexpansion

rem Single authoritative build. ALWAYS builds, tests, publishes both architectures, builds
rem the installer and refreshes dist\, so every run leaves its artifacts in dist\. A "release" argument
rem is still accepted but no longer changes anything.
rem
rem dist\ holds ONLY final artifacts. Publishing writes to build\publish\ first, so a leftover
rem or running file from a previous publish can never masquerade as a fresh artifact.

set ROOT=%~dp0
set DIST=%ROOT%dist
set STAGE=%ROOT%build\publish
set CONFIG=Release

rem Nobody is watching a build: the app must never open a dialog that waits for a click here (one
rem would stop the test step until someone dismissed it).
set WU_UNATTENDED=1

echo === build ===
dotnet build "%ROOT%UltimateUpdater.slnx" -c %CONFIG%
if errorlevel 1 goto :error

echo.
echo === test ===
rem Delayed expansion eats the "!" in the filter, and the whole suite ran instead of the gate - so this
rem one command runs with it switched off. endlocal keeps the errorlevel, which is all that is needed here.
rem LiveButtonContrastTests is excluded alongside WindowLayoutTests: it renders through the SAME WpfHarness
rem (ApplicationThemeManager.Apply loads a WPF-UI theme dictionary via a "pack:" URI), so it is exposed to
rem the identical environmentally-flaky registration this project already treats as non-gating - it failed
rem outright in a scripted build.bat run while `dotnet test` alone passed clean (observed 2026-07-30).
rem The blame-hang flags are the upper bound this step had none of: a test that stops making progress
rem (a WPF harness dispatcher that stopped pumping did exactly this and held a testhost.exe for 38
rem minutes) is killed after two minutes and the run names the test it was in. Dump collection is off -
rem the name is the whole point here, and a full dump of a WPF test host is hundreds of megabytes.
setlocal disabledelayedexpansion
dotnet test "%ROOT%tests\UltimateUpdater.Tests\UltimateUpdater.Tests.csproj" -c %CONFIG% --blame-hang --blame-hang-timeout 2min --blame-hang-dump-type none --filter "FullyQualifiedName!~WindowLayoutTests&FullyQualifiedName!~LiveButtonContrastTests&FullyQualifiedName!~PressedPixelTests"
endlocal
if errorlevel 1 goto :error

rem The UI snapshots are a LOOKING tool, not a correctness gate: WindowLayoutTests renders the real windows
rem to PNGs so a person can see the layout, and LiveButtonContrastTests reads real resolved colours through
rem the same renderer, and PressedPixelTests renders every control pressed and counts near-black pixels.
rem All three depend on the machine's state and have been observed to fail for a whole session
rem and then work again, on the very same code - verified by running against an untouched checkout. Blocking
rem a release on either would mean a build that cannot be trusted to mean anything, so they run separately
rem and only WARN. Read the warning: when they fail, that part was NOT checked this run.
rem No parenthesised if-block here: one of those swallowed the whole icon/version/dist-reset section that
rem follows and the build silently produced a Setup with an empty version in its name. GOTO past the
rem warning instead - the same reason this file avoids CALL throughout: a called label runs in its
rem own scope, and an errorlevel set inside it does not reliably reach the caller.
echo.
echo === ui snapshots + live style checks (not a gate) ===
rem Same hang guard as the gate above, and it matters more here: these three renders are the ones that
rem have actually hung, and a warning-only step must never be able to stop the build for ever.
dotnet test "%ROOT%tests\UltimateUpdater.Tests\UltimateUpdater.Tests.csproj" -c %CONFIG% --blame-hang --blame-hang-timeout 2min --blame-hang-dump-type none --filter "FullyQualifiedName~WindowLayoutTests|FullyQualifiedName~LiveButtonContrastTests|FullyQualifiedName~PressedPixelTests"
if not errorlevel 1 goto :snapshots_done
echo.
echo WARNING: the UI snapshot/live-style renders did not pass - that part was NOT verified this run.
echo          The build continues; look at tests\...\ui-snapshots\ or re-run to check the UI.
:snapshots_done
rem errorlevel survives until something else sets it, so a failed snapshot run would make the NEXT
rem "if errorlevel 1 goto :error" fire and abort a perfectly good build. Clear it deliberately.
ver > nul

echo.
echo === icon ===
python "%ROOT%tools\make-icon.py"
if errorlevel 1 goto :error

rem Version comes from the csproj alone, so it is read back rather than repeated.
rem token 3 is the value: "  <Version>" | "Version" | "0.1.0" | "/Version"
set VERSION=
for /f "tokens=3 delims=<>" %%v in ('findstr /r "<Version>" "%ROOT%src\UltimateUpdater\UltimateUpdater.csproj"') do set VERSION=%%v
if "%VERSION%"=="" (
    echo ERROR: could not read ^<Version^> from the csproj.
    goto :error
)
echo Version: %VERSION%

rem Inlined rather than a CALL'd subroutine: CALL to a label defined later in the file proved
rem unreliable in this exact script after the tasklist/version FOR /F loops above it (a known
rem cmd.exe quirk around CALL following command-substitution FOR /F) - GOTO :error, used
rem everywhere else in this file, is unaffected and keeps working normally.
rem A copy started from dist\ or build\publish\ keeps its exe locked: Windows refuses to delete a
rem running image but allows renaming it. So whatever cannot be deleted is moved into build\stale\
rem (same drive, so the move is a rename) and the folder is cleared anyway; the running copy keeps
rem working from its new place. build\stale\ is swept at the next build, minus anything still running.
if exist "%ROOT%build\stale" rmdir /s /q "%ROOT%build\stale" 2>nul
for %%d in ("%STAGE%" "%DIST%") do (
    powershell -NoProfile -Command "$d='%%~d'; $s='%ROOT%build\stale'; if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }; if (Test-Path -LiteralPath $d) { New-Item -ItemType Directory -Force -Path $s | Out-Null; Get-ChildItem -LiteralPath $d -Recurse -File | ForEach-Object { Move-Item -LiteralPath $_.FullName -Destination (Join-Path $s ([guid]::NewGuid().ToString('N') + '-' + $_.Name)) -ErrorAction SilentlyContinue }; Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }; if (Test-Path -LiteralPath $d) { exit 1 }"
    if errorlevel 1 (
        echo ERROR: could not clear %%d
        echo        A file there is open in another program, for example an Explorer preview.
        goto :error
    )
    mkdir %%d
    if errorlevel 1 (
        echo ERROR: could not create %%d
        goto :error
    )
)

for %%a in (x64 arm64) do (
    echo.
    echo === publish win-%%a ===
    rem Self-contained single file: no runtime prerequisite on the target machine.
    rem PublishTrimmed is deliberately NOT used - WPF does not support it.
    rem IncludeNativeLibrariesForSelfExtract is REQUIRED for the portable exe: without it WPF's native DLLs stay next to
    rem the exe and the "portable" exe dies with DllNotFoundException as soon as it is moved alone.
    rem
    rem EnableCompressionInSingleFile is deliberately NOT used, so the exe is never mistaken for malware.
    rem It deflate-compresses the whole payload into the exe, which leaves a
    rem file with no readable strings and the entropy profile of a packer - exactly what the generic
    rem machine-learning detections (Trojan:Win64/Lazy!MTB and friends) are built to catch, and the single
    rem most reported cause of false positives on self-contained .NET apps. The exe gets larger; the
    rem installer barely changes, because Inno compresses its payload with LZMA2 itself. Startup is also
    rem marginally faster, because nothing has to be decompressed first.
    dotnet publish "%ROOT%src\UltimateUpdater\UltimateUpdater.csproj" ^
        -c %CONFIG% -r win-%%a --self-contained ^
        -p:PublishSingleFile=true ^
        -p:IncludeNativeLibrariesForSelfExtract=true ^
        -p:DebugType=pdbonly ^
        -o "%STAGE%\win-%%a"
    if errorlevel 1 goto :error

    if not exist "%STAGE%\win-%%a\UltimateUpdater.exe" (
        echo ERROR: publish produced no executable for win-%%a.
        goto :error
    )

    rem The installer payload is published a second time WITHOUT self-extraction: WPF's native DLLs then
    rem sit beside the exe in the install folder, instead of being unpacked into a .net folder under the
    rem temp directory, which the unelevated user owns and the elevated worker would reuse. Portable mode
    rem keeps the self-extracting exe above.
    dotnet publish "%ROOT%src\UltimateUpdater\UltimateUpdater.csproj" ^
        -c %CONFIG% -r win-%%a --self-contained ^
        -p:PublishSingleFile=true ^
        -p:IncludeNativeLibrariesForSelfExtract=false ^
        -p:DebugType=pdbonly ^
        -o "%STAGE%\installer-win-%%a"
    if errorlevel 1 goto :error

    if not exist "%STAGE%\installer-win-%%a\UltimateUpdater.exe" (
        echo ERROR: installer publish produced no executable for win-%%a.
        goto :error
    )

    rem dist\ ships ONE portable exe, and it is x64: Windows 11 is 64-bit only and
    rem ARM64 users take the installer, which bundles both. The arm64 publish still happens above so the
    rem installer can carry it, but it is sourced straight from build\publish\ and never lands in dist\.
    rem Named "Ultimate Updater.exe" - fixed, no version, architecture or "Portable" in the name:
    rem it is always x64, and each GitHub Release keeps its own copy under its own tag, so
    rem nothing collides across versions.
    if /i "%%a"=="x64" (
        copy /y "%STAGE%\win-%%a\UltimateUpdater.exe" "%DIST%\Ultimate Updater.exe" >nul
        if errorlevel 1 (
            echo ERROR: could not copy the win-%%a executable into dist.
            goto :error
        )
    )

    rem The .pdb deliberately stays in build\publish\ and out of dist\. Symbols embed absolute source
    rem paths from this machine, and dist\ is the folder that gets uploaded on release. They remain
    rem next to the published exe, where crash analysis can still find them.
)

echo.
echo === signing ===
rem Signing is what actually stops a scanner from treating this as an unknown binary, and it is the one
rem measure that works for every user rather than only for the ones who click through the warning. It is
rem opt-in because it needs a certificate: set WU_SIGN_THUMBPRINT to the SHA1 thumbprint of a code signing
rem certificate in the current user's store and every artifact is signed and timestamped. Nothing about the
rem certificate is ever written into the repository - the thumbprint alone selects it, the private key never
rem leaves the store, and no password appears on a command line.
rem
rem Without it the build carries on and says so: an unsigned build is still a correct build, and refusing to
rem produce one would leave the project with no artifacts at all until a certificate is bought.
if not defined WU_SIGN_THUMBPRINT goto :nosign

set SIGNTOOL=
for /f "delims=" %%i in ('where signtool 2^>nul') do set "SIGNTOOL=%%i"
if not defined SIGNTOOL for /f "delims=" %%i in ('dir /b /s "%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\signtool.exe" 2^>nul') do set "SIGNTOOL=%%i"
if defined SIGNTOOL goto :havesigntool
echo ERROR: WU_SIGN_THUMBPRINT is set but signtool.exe was not found ^(install the Windows SDK^).
goto :error

:havesigntool
if not defined WU_SIGN_TIMESTAMP_URL set "WU_SIGN_TIMESTAMP_URL=http://timestamp.digicert.com"
echo Using %SIGNTOOL%
rem The staged executables are signed BEFORE the installer is built, so the installer carries signed
rem payloads rather than wrapping unsigned ones.
"%SIGNTOOL%" sign /sha1 %WU_SIGN_THUMBPRINT% /fd SHA256 /td SHA256 /tr "%WU_SIGN_TIMESTAMP_URL%" "%STAGE%\win-x64\UltimateUpdater.exe" "%STAGE%\win-arm64\UltimateUpdater.exe" "%STAGE%\installer-win-x64\UltimateUpdater.exe" "%STAGE%\installer-win-arm64\UltimateUpdater.exe"
if errorlevel 1 goto :error

rem dist\ took its copy inside the publish loop, before this ran - refresh it so the shipped portable
rem executable is the signed one.
copy /y "%STAGE%\win-x64\UltimateUpdater.exe" "%DIST%\Ultimate Updater.exe" >nul
if errorlevel 1 goto :error
goto :signdone

:nosign
echo No WU_SIGN_THUMBPRINT set - artifacts stay unsigned.

:signdone

echo.
echo === installer ===
rem winget installs Inno Setup per USER by default, which puts it nowhere near PATH. Looking only at
rem PATH meant a release quietly shipped without its installer on a machine that has the tool.
set "PF86=%ProgramFiles(x86)%"
set ISCC=
for /f "delims=" %%i in ('where iscc 2^>nul') do set "ISCC=%%i"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%PF86%\Inno Setup 6\ISCC.exe" set "ISCC=%PF86%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo WARNING: Inno Setup ^(ISCC.exe^) not found - skipping the installer.
) else (
    echo Using %ISCC%
    "%ISCC%" /DAppVersion=%VERSION% "%ROOT%installer\UltimateUpdater.iss"
    if errorlevel 1 goto :error
)

rem And the installer itself, which is the file most users actually download.
if not defined WU_SIGN_THUMBPRINT goto :nosignsetup
if not exist "%DIST%\Setup-UltimateUpdater-%VERSION%.exe" goto :nosignsetup
"%SIGNTOOL%" sign /sha1 %WU_SIGN_THUMBPRINT% /fd SHA256 /td SHA256 /tr "%WU_SIGN_TIMESTAMP_URL%" "%DIST%\Setup-UltimateUpdater-%VERSION%.exe"
if errorlevel 1 goto :error
:nosignsetup

echo.
echo === dependency list ===
rem NOTICE-dependencies.md is the human-written list of direct packages and their licences; this is
rem the automated, transitive-inclusive one for anyone who wants the full tree. --format json needs a
rem newer SDK than this project otherwise requires; without it this step writes dotnet's plain text
rem listing to the same file name instead and says so, rather than failing the build over a report
rem format nobody actually depends on.
dotnet list "%ROOT%UltimateUpdater.slnx" package --include-transitive --format json > "%DIST%\sbom.json"
if not errorlevel 1 goto :sbom_done
echo --format json is not available on this SDK - writing the plain text dependency listing instead.
dotnet list "%ROOT%UltimateUpdater.slnx" package --include-transitive > "%DIST%\sbom.json"
if errorlevel 1 goto :error
:sbom_done
if not exist "%DIST%\sbom.json" (
    echo ERROR: no dependency list was produced.
    goto :error
)

echo.
echo === checksums ===
rem One SHA256SUMS.txt over every shipped artifact; the self-updater verifies against it.
rem Get-FileHash is used rather than certutil, whose output is localized and would break parsing.
powershell -NoProfile -Command ^
    "Get-ChildItem -Path '%DIST%\*.exe' | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } | Set-Content -Path '%DIST%\SHA256SUMS.txt' -Encoding ascii"
if errorlevel 1 goto :error
if not exist "%DIST%\SHA256SUMS.txt" (
    echo ERROR: no checksum file was produced.
    goto :error
)

echo.
echo Artifacts in %DIST%
type "%DIST%\SHA256SUMS.txt"

echo.
echo BUILD OK - artifacts are in dist\.
endlocal
exit /b 0

:error
echo.
echo BUILD FAILED
rem Keep the window open on failure so the error stays readable, but ONLY when build.bat was launched
rem by a double-click in Explorer (which runs it as cmd /c "..."), never in an automated run: the
rem autobuild Stop hook and CI set WU_NOPAUSE=1, and a run started from an existing console keeps its
rem own window open anyway. CMDCMDLINE carries the /c switch only in the double-click case, and findstr
rem looks for that literal switch.
if not "%WU_NOPAUSE%"=="1" (
    echo %CMDCMDLINE% | findstr /i /c:"/c" >nul && pause
)
endlocal
exit /b 1
