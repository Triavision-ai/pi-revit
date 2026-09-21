# Operation receipt behavior

```powershell
dotnet run --project tests/operation-store/operation-store-tests.csproj
```

This harness compiles production `OperationStore.cs` directly, without substitutes. Checks cover concurrent duplicate reservations, canonical argument ordering and UUID spelling, conflicting reuse, receipt states, expiration before execution, bounded result retention, permanent no-replay tombstones, receipt capacity, generation validation, honest unknown outcomes, detached cached values and serialization failures.

Expiry cases use already-expired deadlines rather than sleeps. No Revit, bridge, network or model operations occur.
