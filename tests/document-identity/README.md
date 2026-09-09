# Offline document identity checks

```powershell
dotnet run --project tests/document-identity/document-identity-tests.csproj
dotnet run --project tests/document-identity/document-identity-tests.csproj -- --baseline
```

The normal run compiles the complete production `DocumentGuard.cs` directly. It
uses small fake documents whose separate managed wrappers compare equal only
when they share one native open-document lifetime. Closing invalidates that
lifetime, and reopening creates a new lifetime even when the title is unchanged.
These semantics follow the installed Revit 2025 API's Document.Equals and
GetHashCode documentation; actual Revit behavior still requires live tests.

The baseline run intentionally fails three protection assertions against the
original guard extracted from `ToolSupport.cs` at commit
`fc9d78554760ae27df4c1f4fcac29aae2db9179f`, blob
`3d1e1d3f02b1ebb16ec8366009c4d4f012cc3254`. Only its class name is changed so it
can coexist with the production guard. The baseline does not implement a new
imitation of the old matching logic.

Checks cover same-native wrappers, independent same-title documents, detached
titles, invalid/missing IDs, all currently required operations, selection-get
with temporary isolation, optional read calls, stale identities after reopen,
and the additional human-readable title check. No Revit bridge, Revit process,
model, or network connection is touched.
