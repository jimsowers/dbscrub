@echo off
REM dbscrub launcher for cmd.exe. See dbscrub.ps1 for the PowerShell version
REM and for why this exists at all.
REM
REM Usage:  dbscrub report --server localhost --database MyDb --config my.json
REM
REM Kept to plain ASCII on purpose: cmd reads batch files in the console code
REM page, and a stray non-ASCII character in a comment can be parsed as a
REM command and print a spurious error before anything runs.

dotnet run --project "%~dp0src\DbScrub.Cli" -- %*

exit /b %ERRORLEVEL%
