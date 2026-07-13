@echo off
rem pi-revit: open Pi in the pi-revit workspace from anywhere. All arguments are
rem passed straight to pi (e.g. pi-revit -c continues the last session).
rem Model output needs no foldering decision: the Revit tools file each export
rem under Models\<model title>\ automatically, keyed by the document it came from.
rem Installed next to the pi command by scripts\setup-workspace.ps1, which rewrites
rem the WORKSPACE line below to the chosen -WorkspaceDir.

set "WORKSPACE=%USERPROFILE%\Documents\pi-revit"

cd /d "%WORKSPACE%"
pi %*
