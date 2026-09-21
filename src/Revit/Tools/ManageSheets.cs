using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitBridge.Tools;

internal sealed class ManageSheets : ITool
{
    public string Name => "manage_sheets";
    public string Label => "Manage Sheets";
    public string Tier => "advanced";
    public bool Write => true;
    public string Description => "Create or rename/renumber a drawing sheet. Creation requires name and number; optional titleblock_type_id chooses a loaded titleblock FamilySymbol, otherwise creates a sheet without a titleblock. Update requires sheet_id and accepts name/number. Revit validates unique sheet numbers and valid text; sheet names may repeat; the call rolls back on failure. preview=true commit-validates and rolls back, and new preview sheet IDs are temporary. Use manage_sheet_placements for views/schedules, get_elements for querying sheets, and delete_elements for removal.";
    public object ParametersSchema => new
    {
        type = "object", properties = new
        {
            action = new { type = "string", @enum = new[] { "create", "update" } },
            sheet_id = new { type = "integer", minimum = 1 }, name = new { type = "string", minLength = 1 }, number = new { type = "string", minLength = 1 },
            titleblock_type_id = new { type = "integer", minimum = 1, description = "Creation only; omitted means no titleblock." }, preview = ModelEditInputs.PreviewSchema,
        }, required = new[] { "action" },
    };
    public object Execute(JsonElement args, ToolContext context)
    {
        var doc = context.Document ?? throw new NoActiveDocumentException();
        string action = JsonArgs.GetString(args, "action") ?? "";
        if (action is not ("create" or "update")) throw new ArgumentException("action must be create or update.");
        string? ReadText(string key)
        {
            if (!args.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException($"{key} must be a nonempty string.");
            return value.GetString();
        }
        string? name = ReadText("name"), number = ReadText("number");
        if (action == "create" && (name == null || number == null)) throw new ArgumentException("Sheet creation requires name and number.");
        if (action == "update" && args.TryGetProperty("titleblock_type_id", out _)) throw new ArgumentException("titleblock_type_id is only supported when creating a sheet; use change_element_types on an existing titleblock instance.");
        return ModelEditBatch.Run(doc, Name, args, new[] { new ModelEditBatch.Step(new() { ["action"] = action }, () =>
        {
            ViewSheet sheet;
            object? before = null;
            if (action == "create")
            {
                var typeId = ElementId.InvalidElementId;
                if (args.TryGetProperty("titleblock_type_id", out _))
                {
                    typeId = new ElementId(JsonArgs.GetLong(args, "titleblock_type_id") ?? 0);
                    var type = doc.GetElement(typeId) as FamilySymbol;
                    if (type?.Category?.Id.Value != (long)BuiltInCategory.OST_TitleBlocks) throw new ArgumentException("titleblock_type_id must identify a titleblock FamilySymbol.");
                }
                sheet = ViewSheet.Create(doc, typeId);
            }
            else
            {
                sheet = doc.GetElement(new ElementId(JsonArgs.GetLong(args, "sheet_id") ?? 0)) as ViewSheet ?? throw new ArgumentException("sheet_id is not a sheet.");
                before = Describe(sheet);
            }
            if (number != null) sheet.SheetNumber = number;
            if (name != null) sheet.Name = name;
            doc.Regenerate();
            return new() { ["before"] = before, ["sheet"] = Describe(sheet), ["created"] = action == "create", ["id_is_temporary"] = action == "create" && JsonArgs.GetBool(args, "preview", false) };
        }) }).Payload;
    }
    private static object Describe(ViewSheet sheet) => new { id = sheet.Id.Value, unique_id = sheet.UniqueId, name = sheet.Name, number = sheet.SheetNumber, placeholder = sheet.IsPlaceholder };
}
