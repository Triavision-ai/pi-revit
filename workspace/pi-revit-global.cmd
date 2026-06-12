@echo off
rem pi-revit: open Pi in the pi-revit workspace from anywhere.
rem   pi-revit              -> Documents\pi-revit (workspace AGENTS.md loads)
rem   pi-revit <project>    -> Documents\pi-revit\Projects\<project> (its AGENTS.md loads too)
rem   any further args are passed straight to pi (e.g. pi-revit sample -c)
rem Installed next to the pi command by scripts\setup-workspace.ps1.

set "WORKSPACE=%USERPROFILE%\Documents\pi-revit"

if "%~1"=="" (
    cd /d "%WORKSPACE%"
    pi
    exit /b
)

if exist "%WORKSPACE%\Projects\%~1\" (
    cd /d "%WORKSPACE%\Projects\%~1"
    shift
) else (
    cd /d "%WORKSPACE%"
)

set "ARGS="
:collect
if not "%~1"=="" (
    set "ARGS=%ARGS% %1"
    shift
    goto collect
)
pi%ARGS%
