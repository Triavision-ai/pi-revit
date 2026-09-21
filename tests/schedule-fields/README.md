# Schedule field identity regression

Run from the repository root:

```powershell
dotnet run --project tests/schedule-fields/schedule-fields-tests.csproj
```

This offline harness compiles the production `GetScheduleFields.cs` and
`ManageSchedules.cs`. It sends discovered field identities into the editing tool
and checks the resulting fields and subsequent discovery. Its fixture includes
Count with parameter ID `-1152353`, as observed in Revit, and ordinary fields that
share a parameter ID but have different field types. It also covers schedules
whose discovery does not advertise Count and invalid explicit Count identities.

Minimal Revit substitutes supply field availability and store added fields. The
transaction wrapper runs each production step directly; this suite does not
establish Revit API behavior, transaction rollback, schedule rendering or quantity
accuracy. Those require the existing transaction tests and live Revit checks.
Unsupported fixture operations throw rather than silently simulate success.

For a red-test check against a saved pre-fix source file, use
`-p:ManageSchedulesSource=<absolute-path-to-ManageSchedules.cs>`. The normal run
always uses the current production source.
