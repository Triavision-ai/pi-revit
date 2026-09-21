using System.Text.Json;
using Autodesk.Revit.DB;
using RevitBridge.Tools;

int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}

void Bounds(BoundingBoxXYZ box, Transform placement, double[] expectedMin, double[] expectedMax)
{
    var result = JsonSerializer.SerializeToElement(GetLinkedElements.HostBounds(box, placement));
    foreach (var (key, expected) in new[] { ("min", expectedMin), ("max", expectedMax) })
    {
        var actual = result.GetProperty(key).EnumerateArray().Select(value => value.GetDouble()).ToArray();
        if (actual.Length != 3) throw new Exception($"{key} must contain three coordinates.");
        for (int axis = 0; axis < 3; axis++)
            if (!double.IsFinite(actual[axis]) || Math.Abs(actual[axis] - expected[axis]) > 1e-10)
                throw new Exception($"{key}[{axis}]: expected {expected[axis]:R}, received {actual[axis]:R}.");
    }
}

Check("45-degree rectangular rotation includes extrema outside transformed min/max", () =>
{
    // x' = (x-y)/sqrt(2), y' = (x+y)/sqrt(2).
    // On [0,4] x [0,2], x' spans [-sqrt(2),2sqrt(2)], y' spans [0,3sqrt(2)].
    double root2 = Math.Sqrt(2);
    Bounds(new() { Min = new(0, 0, 0), Max = new(4, 2, 3) },
        Transform.RotationZ(Math.PI / 4), [-root2, 0, 0], [2 * root2, 3 * root2, 3]);
});

Check("translation preserves negative coordinates and asymmetric extents", () =>
    Bounds(new() { Min = new(-8, -6, -4), Max = new(-2, -1, -0.5) },
        Transform.Translation(3, -10, 2), [-5, -16, -2], [1, -11, 1.5]));

Check("rotation and translation compose around a centered rectangle", () =>
{
    // A centered 4-by-2 rectangle rotated 45 degrees has half-width and half-height 3/sqrt(2).
    double extent = 3 / Math.Sqrt(2);
    Bounds(new() { Min = new(-2, -1, -3), Max = new(2, 1, 5) },
        Transform.RotationZ(Math.PI / 4, new(10, -7, 4)),
        [10 - extent, -7 - extent, 1], [10 + extent, -7 + extent, 9]);
});

Check("box transform is applied before link placement", () =>
{
    // Two quarter turns produce (-x,-y,z). The translated box origin (3,4,5)
    // maps through the second quarter turn plus (10,20,30) to (6,23,35).
    Bounds(new()
    {
        Min = new(-1, -2, -3), Max = new(1, 2, 3),
        Transform = Transform.RotationZ(Math.PI / 2, new(3, 4, 5)),
    }, Transform.RotationZ(Math.PI / 2, new(10, 20, 30)), [5, 21, 32], [7, 25, 38]);
});

Check("box transform can exchange vertical and horizontal axes", () =>
{
    // Quarter turn about X: (x,y,z) -> (x,-z,y), then translate by (-5,2,-9).
    Bounds(new()
    {
        Min = new(-2, -3, -4), Max = new(1, 5, 6),
        Transform = new(new(1, 0, 0), new(0, 0, 1), new(0, -1, 0), new(0, 0, 0)),
    }, Transform.Translation(-5, 2, -9), [-7, -4, -12], [-4, 6, -4]);
});

Check("identity preserves a wholly negative box", () =>
    Bounds(new() { Min = new(-9, -8, -7), Max = new(-3, -2, -1) },
        Transform.Identity, [-9, -8, -7], [-3, -2, -1]));

Check("missing geometry returns null", () =>
{
    if (GetLinkedElements.HostBounds(null, Transform.Translation(10, 20, 30)) is not null)
        throw new Exception("A null bounding box must remain null.");
});

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Point(XYZ actual, XYZ expected)
{
    foreach (var (value, target) in new[] { (actual.X, expected.X), (actual.Y, expected.Y), (actual.Z, expected.Z) })
        Require(double.IsFinite(value) && Math.Abs(value - target) < 1e-10, $"expected coordinate {target:R}, received {value:R}");
}

Check("SpatialBounds transforms all corners of a translated 45-degree rectangle", () =>
{
    double root2 = Math.Sqrt(2);
    var bounds = SpatialBounds.Of(new Element
    {
        Bounds = new() { Min = new(0, 0, 0), Max = new(4, 2, 3), Transform = Transform.RotationZ(Math.PI / 4, new(-10, -20, -30)) },
    }) ?? throw new Exception("expected element bounds");
    // Analytical ranges of x-y and x+y, then translation; no corner-enumeration oracle.
    Point(bounds.Min, new(-10 - root2, -20, -30));
    Point(bounds.Max, new(-10 + 2 * root2, -20 + 3 * root2, -27));
});

Check("SpatialBounds includes extrema after a rotation that changes vertical coordinates", () =>
{
    var bounds = SpatialBounds.Of(new Element
    {
        Bounds = new()
        {
            Min = new(-2, -3, -4), Max = new(1, 5, 6),
            Transform = new(new(1, 0, 0), new(0, 0, 1), new(0, -1, 0), new(-5, 2, -9)),
        },
    }) ?? throw new Exception("expected element bounds");
    // (x,y,z) -> (x-5,2-z,y-9).
    Point(bounds.Min, new(-7, -4, -12));
    Point(bounds.Max, new(-4, 6, -4));
});

var outer = new SpatialBounds(new(-2, -2, -2), new(2, 2, 2));
Check("SpatialBounds distinguishes partial intersection from full containment", () =>
{
    var partial = new SpatialBounds(new(1, -1, -1), new(4, 1, 1));
    Require(outer.Intersects(partial) && partial.Intersects(outer), "intersection must be symmetric");
    Require(!outer.Contains(partial) && !partial.Contains(outer), "partly overlapping boxes must not contain one another");
});

Check("nested and identical boxes have zero gap with directional containment", () =>
{
    var inner = new SpatialBounds(new(-1, -1, -1), new(1, 1, 1));
    Require(outer.Contains(inner) && !inner.Contains(outer), "containment direction is incorrect");
    Require(outer.Contains(outer) && outer.Intersects(outer), "identical bounds must contain and intersect themselves");
    Point(outer.Gap(inner), new(0, 0, 0));
    Point(inner.Gap(outer), new(0, 0, 0));
});

foreach (var (name, min) in new[] { ("face", new XYZ(2, -1, -1)), ("edge", new XYZ(2, 2, -1)), ("corner", new XYZ(2, 2, 2)) })
Check($"SpatialBounds treats {name} contact as intersection with zero gap", () =>
{
    var contact = new SpatialBounds(min, new(3, 3, 3));
    Require(outer.Intersects(contact) && contact.Intersects(outer), "boundary contact must count as intersection");
    Require(!outer.Contains(contact), "touching outside bounds must not count as contained");
    Point(outer.Gap(contact), new(0, 0, 0));
});

foreach (var (axis, min, expectedGap) in new[]
{
    ("X", new XYZ(3, -1, -1), new XYZ(1, 0, 0)),
    ("Y", new XYZ(-1, 3, -1), new XYZ(0, 1, 0)),
    ("Z", new XYZ(-1, -1, 3), new XYZ(0, 0, 1)),
})
Check($"SpatialBounds rejects separation on {axis} and measures its one-axis gap", () =>
{
    var separated = new SpatialBounds(min, new(4, 4, 4));
    Require(!outer.Intersects(separated) && !separated.Intersects(outer), "separation on one axis must rule out intersection");
    Require(!outer.Contains(separated), "separated bounds cannot be contained");
    Point(outer.Gap(separated), expectedGap);
    Point(separated.Gap(outer), expectedGap);
});

Check("negative-coordinate boxes produce a symmetric 3-4-5 gap", () =>
{
    var a = new SpatialBounds(new(-10, -10, -10), new(-9, -9, -9));
    var b = new SpatialBounds(new(-6, -5, -10), new(-5, -4, -9));
    var gap = a.Gap(b);
    Point(gap, new(3, 4, 0));
    Point(b.Gap(a), new(3, 4, 0));
    Require(Math.Abs(Math.Sqrt(gap.X * gap.X + gap.Y * gap.Y + gap.Z * gap.Z) - 5) < 1e-10, "expected Euclidean box gap of five units");
});

Check("flat bounds on an enclosing boundary remain contained", () =>
{
    var flat = new SpatialBounds(new(-2, -1, 2), new(2, 1, 2));
    Require(outer.Contains(flat) && outer.Intersects(flat), "boundary containment must be inclusive even for flat geometry");
    Point(outer.Gap(flat), new(0, 0, 0));
});

Check("SpatialBounds returns null for elements without model geometry", () =>
    Require(SpatialBounds.Of(new Element { Bounds = null }) is null, "missing bounds must not become a box at the origin"));

Check("SpatialBounds description converts internal feet to requested length units", () =>
{
    var bounds = new SpatialBounds(new(-1, 0, 2), new(1, 3, 4));
    var described = JsonSerializer.SerializeToElement(bounds.Describe(1 / 304.8));
    var min = described.GetProperty("min").EnumerateArray().Select(p => p.GetDouble()).ToArray();
    var max = described.GetProperty("max").EnumerateArray().Select(p => p.GetDouble()).ToArray();
    Point(new(min[0], min[1], min[2]), new(-304.8, 0, 609.6));
    Point(new(max[0], max[1], max[2]), new(304.8, 914.4, 1219.2));
});

Console.WriteLine($"{passed} passed, {failed} failed; production HostBounds and SpatialBounds, offline geometry substitutes, no Revit or model operations.");
return failed == 0 ? 0 : 1;
