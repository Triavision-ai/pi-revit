using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// Shared classification of special or system-owned objects (inv:special-objects-flagged).
    /// Tools never mix these silently into ordinary results: they either flag them here or
    /// exclude them and count the exclusion. Only non-default traits are emitted, so
    /// ordinary elements add nothing to a result. New tools reuse this instead of inventing
    /// their own checks, and new trait kinds are added here once.
    /// </summary>
    internal static class ElementTraits
    {
        /// <summary>Non-default traits of an element, or null when it has none.</summary>
        public static Dictionary<string, object>? For(Element element)
        {
            var traits = new Dictionary<string, object>();
            if (element is ScheduleSheetInstance { IsTitleblockRevisionSchedule: true })
                traits["titleblock_revision_schedule"] = true;
            if (element is ViewSchedule { IsTitleblockRevisionSchedule: true })
                traits["titleblock_revision_schedule"] = true;
            if (element is ViewSheet { IsPlaceholder: true })
                traits["placeholder_sheet"] = true;
            if (element is View view)
            {
                if (view.IsTemplate)
                    traits["view_template"] = true;
                var primary = view.GetPrimaryViewId();
                if (primary != null && primary != ElementId.InvalidElementId)
                    traits["dependent_view_of"] = primary.Value;
            }
            if (element.GroupId is { } group && group != ElementId.InvalidElementId)
                traits["group_id"] = group.Value;
            if (element.DesignOption is { } option)
                traits["design_option_id"] = option.Id.Value;
            if (element.Pinned)
                traits["pinned"] = true;
            return traits.Count > 0 ? traits : null;
        }

        /// <summary>
        /// Kind of a sheet placement. A titleblock revision schedule is part of the titleblock,
        /// not content someone placed, so it has its own kind and cannot be moved.
        /// </summary>
        public static string PlacementKind(Element placement) => placement switch
        {
            Viewport => "viewport",
            ScheduleSheetInstance { IsTitleblockRevisionSchedule: true } => "titleblock_revision_schedule",
            ScheduleSheetInstance => "schedule",
            _ => "other",
        };
    }
}
