@echo off
rem Runs one half of the non-gating UI snapshot and live style tests and records the outcome for build.bat.
rem build.bat starts both halves in the background so the renders run while the gating tests run.
rem   run-snapshots.cmd <test csproj> <configuration> <log file> <result file> <shard 1|2>
rem The halves split WindowLayoutTests by the first letter of the test method, chosen so both take about
rem the same time. Shard 2 is "everything shard 1 does not take", so a new test always runs somewhere.
setlocal disabledelayedexpansion
set "W=FullyQualifiedName~UltimateUpdater.Tests.WindowLayoutTests"
set "N=FullyQualifiedName!~UltimateUpdater.Tests.WindowLayoutTests"
if "%~5"=="1" set "FILTER=%W%.M|%W%.S|%W%.Q|%W%.R|%W%.V|%W%.P"
if "%~5"=="2" set "FILTER=(%W%&%N%.M&%N%.S&%N%.Q&%N%.R&%N%.V&%N%.P)|FullyQualifiedName~LiveButtonContrastTests|FullyQualifiedName~PressedPixelTests"
if not defined FILTER (
    > "%~4" echo fail
    exit /b 1
)
dotnet test "%~1" -c %2 --no-build --nologo --blame-hang --blame-hang-timeout 2min --blame-hang-dump-type none --filter "%FILTER%" > "%~3" 2>&1
if errorlevel 1 (
    > "%~4" echo fail
) else (
    > "%~4" echo ok
)
endlocal
