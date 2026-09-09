# Element query regressions

Run `dotnet run --project tests/element-query-regressions` from the checkout.

The project links the complete production `GetElements.cs` and
`GetElementDetails.cs` files and invokes their actual `Execute` methods. A small
controlled dependency layer supplies fixture elements, parameters, and collectors.
The cross-family fixture deliberately puts a different same-named parameter ID on
element 51, beyond the production probe window. Assertions use fixed expected IDs
and values and cover independent parameter include flags.

This harness verifies production query orchestration and parameter selection. It
does not establish Autodesk collector semantics, the real unit engine, localization,
API object lifetime, transport visibility, or live model correctness. Those require
the documented Revit and Pi workflow retests.
