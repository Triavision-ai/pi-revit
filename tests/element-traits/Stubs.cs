// Controlled substitutes for the few Revit members ElementTraits reads. This is not a
// Revit emulator: each type exposes only the properties the production classifier uses.
namespace Autodesk.Revit.DB
{
    public sealed record ElementId(long Value)
    {
        public static ElementId InvalidElementId { get; } = new(-1);
    }
    public sealed class DesignOption { public ElementId Id { get; init; } = new(900); }
    public class Element
    {
        public ElementId Id { get; init; } = new(1);
        public ElementId? GroupId { get; init; } = ElementId.InvalidElementId;
        public DesignOption? DesignOption { get; init; }
        public bool Pinned { get; init; }
    }
    public class View : Element
    {
        public bool IsTemplate { get; init; }
        public ElementId PrimaryViewId { get; init; } = ElementId.InvalidElementId;
        public ElementId GetPrimaryViewId() => PrimaryViewId;
    }
    public sealed class ViewSheet : View { public bool IsPlaceholder { get; init; } }
    public sealed class ViewSchedule : View { public bool IsTitleblockRevisionSchedule { get; init; } }
    public sealed class Viewport : Element { }
    public sealed class ScheduleSheetInstance : Element { public bool IsTitleblockRevisionSchedule { get; init; } }
}
