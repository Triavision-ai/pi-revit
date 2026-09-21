# Linked geometry regression checks

Run from the repository root:

```powershell
dotnet run --project tests/linked-geometry/linked-geometry-tests.csproj
```

This console harness compiles the complete production `GetLinkedElements.cs` and `SpatialBounds.cs`, directly exercising bounds conversion, intersection, containment and gap calculations. Minimal API substitutes supply affine point transforms and bounding boxes; all unrelated query methods throw if invoked. No Revit installation, model, bridge, or network call is used.

Expected bounds are analytical values for fixed shapes, rather than a second implementation of the eight-corner algorithm. Cases cover a 45-degree rectangular rotation that fails when only Min and Max are transformed, translation, negative coordinates, box-transform composition order, rotation involving the vertical axis, and null geometry. These checks validate bounds mathematics, not native Revit API integration.

Spatial relations additionally cover partial overlap, nested/equal boxes, face/edge/corner contact, separation on every axis, a 3-4-5 diagonal gap, flat boundary containment and output-unit conversion. Bounding-box overlap is not a solid-clash result.
