# Transaction and export behavioral checks

Run from the repository root:

```powershell
dotnet run --project tests/transaction-export/transaction-export-tests.csproj
```

The console test project compiles the actual FailureGuard, ManageSelection and ExportDocuments source files against a fake Revit API. Start resets failure options, simulated commits process errors/warnings, native document equality spans separate wrappers, and fake export calls write small fixture files. Output defaults to a unique temporary directory, never the user's real Documents/pi-revit folder. Pass `--output-root=<directory>` after `--` to preserve evidence in a chosen directory. Every invocation creates its own subdirectory.

To reproduce against an earlier source snapshot, set MSBuild property `RevitToolSource` to that directory and pass `--baseline` to omit checks for newly added helper methods. This does not assert source lines or strings of implementation code.

These tests are not a substitute for Revit integration tests. The fake PDF/IFC files are fixture text, not valid export documents. The production build should also be compiled against the installed Revit API; live commit, script, parameter and rendering behavior must be checked on a disposable model under the active Revit controller.
