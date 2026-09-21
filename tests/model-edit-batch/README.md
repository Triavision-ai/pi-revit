# Model edit batch behavior

```powershell
dotnet run --project tests/model-edit-batch/model-edit-batch-tests.csproj
```

The harness compiles production `ModelEditBatch.cs` directly. Minimal transaction, subtransaction and transaction-group substitutes maintain independent snapshots and expose controllable API return values and final statuses. Failure handling and JSON argument access are substituted; Revit failure preprocessing itself is outside this harness.

Checks cover partial success, failed-step isolation, atomic rollback, preview commit validation followed by group rollback, empty batches, subtransaction rejection, commit rejection, status disagreement, unconfirmed rollbacks, and regeneration failure. Assertions check observable state, result partitioning and operation order. No native Revit calls, model files or saves occur.
