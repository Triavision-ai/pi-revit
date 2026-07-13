@echo off
rem pi-revit: open Pi in the pi-revit workspace from anywhere.
rem   pi-revit              -> Documents\pi-revit (workspace AGENTS.md loads)
rem   pi-revit <project>    -> Documents\pi-revit\Projects\<project>, created with a
rem                            starter AGENTS.md on first use (its AGENTS.md loads too)
rem   any further args are passed straight to pi (e.g. pi-revit sample -c)
rem Installed next to the pi command by scripts\setup-workspace.ps1, which rewrites
rem the WORKSPACE line below to the chosen -WorkspaceDir.

set "WORKSPACE=%USERPROFILE%\Documents\pi-revit"

if "%~1"=="" (
    cd /d "%WORKSPACE%"
    pi
    exit /b
)

set "PROJECT=%WORKSPACE%\Projects\%~1"
if not exist "%PROJECT%\" (
    md "%PROJECT%"
    if errorlevel 1 (
        echo Could not create project folder: "%PROJECT%"
        exit /b 1
    )
    (
        echo # Project: %~1
        echo.
        echo Notes for Pi sessions on this Revit project. Replace/extend as you learn the model.
        echo.
        echo - Model: ^(file name / what it is^)
        echo - Units: ^(metric/imperial^)
        echo - Naming conventions: ^(levels, views, sheets^)
        echo - Known quirks: ^(warnings to ignore, in-place families, etc.^)
        echo - Files: keep outputs in exports\, kept snapshots in captures\, generated code in scripts\
    ) > "%PROJECT%\AGENTS.md"
    echo Created new project: "%PROJECT%"
)
cd /d "%PROJECT%"
shift

set "ARGS="
:collect
if not "%~1"=="" (
    set "ARGS=%ARGS% %1"
    shift
    goto collect
)
pi%ARGS%
