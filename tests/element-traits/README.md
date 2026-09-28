# Element traits

Run from the repository root:

```powershell
dotnet run --project tests/element-traits/element-traits-tests.csproj
```

This offline harness compiles the production `ElementTraits.cs`, the shared classifier for
special or system-owned objects, against minimal Revit substitutes. It checks that
titleblock revision schedules get their own placement kind, and that placeholder sheets,
view templates, dependent views, group and design-option members and pinned elements are
flagged. It also checks that ordinary elements add nothing to a result.

The platform checker (`npm run test:docs`) separately requires every bridge source that
handles `ScheduleSheetInstance` to classify through `ElementTraits`. This suite does not
establish Revit API behavior or sheet contents; those need live checks.
