# Derived state

Run from the repository root:

```powershell
dotnet run --project tests/derived-state/derived-state-tests.csproj
```

This offline harness compiles the Revit-free parts of two shared bridge mechanisms:

- `ChangeSet.cs`: the net change accounting behind `model_changes`. Committed changes are
  reported, an object created and deleted in one call leaves nothing, and a rolled-back
  preview group leaves no net change. A call without document events is "not observed",
  never "no change".
- `InheritedState.Summary.cs`: the inherited-state summary. Hidden categories and elements,
  filters, overrides and template are reported with the check the caller must make, empty
  sections are omitted, long lists are capped with their full count, and an interrupted
  element scan is marked as a lower bound.

The platform checker (`npm run test:docs`) separately requires every bridge tool that
duplicates, copies, mirrors or retypes to use `InheritedState`, every name or sheet-number
assignment to go through `ElementNames`, and the dispatcher to attach `model_changes`. Reading
the facts from Revit and the document-change events are verified live, not here.
