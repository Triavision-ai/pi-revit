using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ManageSheetPlacements : ITool
{
    public string Name => "manage_sheet_placements";
    public string Label => "Manage Sheet Placements";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "List, place or move viewports and schedule instances on a sheet. Positions are paper-space sheet coordinates with explicit length units, [x,y,0]; never multiply by view scale. For viewports position is the box center excluding its label; for schedules it is the insertion point. Place requires sheet_id/view_id, move requires placement_id. Viewports may rotate none/clockwise/counterclockwise; schedule rotation is not supported. preview=true commit-validates then rolls back; new placement IDs are temporary. List includes both viewports and schedules with paging. Exact document identity is required for every action.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            action = new { type = "string", @enum = new[] { "list", "place", "move" } },
            sheet_id = new { type = "integer", minimum = 1 }, view_id = new { type = "integer", minimum = 1 }, placement_id = new { type = "integer", minimum = 1 },
            position = ModelEditInputs.VectorSchema("Sheet paper-space [x,y,0] in unit."), unit = ModelEditInputs.LengthUnitSchema,
            rotation = new { type = "string", @enum = new[] { "none", "clockwise", "counterclockwise" } },
            offset = new { type = "integer", minimum = 0 }, limit = new { type = "integer", minimum = 1, maximum = 200 }, preview = ModelEditInputs.PreviewSchema,
        }, required = new[] { "action" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string action = JsonArgs.GetString(args, "action") ?? "";
        ElementId Id(string name) => new(JsonArgs.GetLong(args, name) is long id && id > 0 ? id : throw new ArgumentException($"{name} must be a positive ID."));
        if (action == "list")
        {
            var sheet = doc.GetElement(Id("sheet_id")) as ViewSheet ?? throw new ArgumentException("sheet_id is not a sheet.");
            var items = new FilteredElementCollector(doc).OwnedByView(sheet.Id).WhereElementIsNotElementType()
                .Where(e => e is Viewport or ScheduleSheetInstance).OrderBy(e => e.Id.Value).ToArray();
            int offset = JsonArgs.GetInt(args, "offset", 0), limit = JsonArgs.GetInt(args, "limit", 100);
            if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Invalid paging range.");
            var page = items.Skip(offset).Take(limit).Select(Describe).ToArray();
            return new { sheet_id = sheet.Id.Value, total_count = items.Length, offset, returned_count = page.Length, next_offset = offset + page.Length < items.Length ? (int?)(offset + page.Length) : null, placements = page, coordinate_system = "sheet_paper", unit = "feet" };
        }
        if (action is not ("place" or "move")) throw new ArgumentException("action must be list, place or move.");
        var point = ModelEditInputs.Vector(args, "position").Multiply(ModelEditInputs.LengthScale(args));
        if (Math.Abs(point.Z) > 1e-9) throw new ArgumentException("Sheet position Z must be zero.");
        var rotation = JsonArgs.GetString(args, "rotation") switch
        {
            null => (ViewportRotation?)null, "none" => ViewportRotation.None, "clockwise" => ViewportRotation.Clockwise,
            "counterclockwise" => ViewportRotation.Counterclockwise, _ => throw new ArgumentException("Invalid rotation."),
        };
        return ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(new() { ["action"] = action }, () =>
        {
            Element placement;
            object? before = null;
            if (action == "place")
            {
                var sheet = doc.GetElement(Id("sheet_id")) as ViewSheet ?? throw new ArgumentException("sheet_id is not a sheet.");
                if (sheet.IsPlaceholder) throw new ArgumentException("Cannot place content on a placeholder sheet.");
                var view = doc.GetElement(Id("view_id")) as View ?? throw new ArgumentException("view_id is not a view or schedule.");
                if (view is ViewSchedule schedule)
                {
                    if (rotation != null) throw new ArgumentException("Schedules do not support viewport rotation.");
                    placement = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, point);
                }
                else
                {
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) throw new ArgumentException("This view cannot be added to that sheet; it may already be placed.");
                    placement = Viewport.Create(doc, sheet.Id, view.Id, point);
                }
            }
            else
            {
                placement = doc.GetElement(Id("placement_id")) ?? throw new ArgumentException("placement_id was not found.");
                if (args.TryGetProperty("sheet_id", out _) && placement.OwnerViewId != Id("sheet_id")) throw new ArgumentException("The placement does not belong to the expected sheet_id.");
                if (placement.Pinned) throw new ArgumentException("Placement is pinned; it was not unpinned.");
                before = Describe(placement);
                if (placement is Viewport viewport) viewport.SetBoxCenter(point);
                else if (placement is ScheduleSheetInstance schedule)
                {
                    if (rotation != null) throw new ArgumentException("Schedules do not support viewport rotation.");
                    schedule.Point = point;
                }
                else throw new ArgumentException("placement_id must identify a viewport or schedule instance.");
            }
            if (rotation != null && placement is Viewport target) target.Rotation = rotation.Value;
            doc.Regenerate();
            if (placement is Viewport positioned) { positioned.SetBoxCenter(point); doc.Regenerate(); }
            return new() { ["before"] = before, ["placement"] = Describe(placement), ["created"] = action == "place", ["id_is_temporary"] = action == "place" && JsonArgs.GetBool(args, "preview", false) };
        }) }).Payload;
    }
    private static object Describe(Element element)
    {
        static double[] Point(XYZ p) => new[] { p.X, p.Y, p.Z };
        return element switch
        {
            Viewport v => new { id = v.Id.Value, unique_id = v.UniqueId, kind = "viewport", sheet_id = v.SheetId.Value, view_id = v.ViewId.Value, position = Point(v.GetBoxCenter()), position_kind = "box_center_excluding_label", rotation = v.Rotation.ToString(), unit = "feet" } as object,
            ScheduleSheetInstance s => new { id = s.Id.Value, unique_id = s.UniqueId, kind = "schedule", sheet_id = s.OwnerViewId.Value, view_id = s.ScheduleId.Value, position = Point(s.Point), position_kind = "insertion_point", unit = "feet" },
            _ => throw new ArgumentException("Element is not a sheet placement."),
        };
    }
}
