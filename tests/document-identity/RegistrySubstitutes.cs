using System.Text.Json;

namespace Autodesk.Revit.UI
{
    internal sealed class UIApplication { }
}

namespace RevitBridge.Tools
{
    // These metadata-only substitutes let the actual registry build its default list.
    // Registry assertions test schema/effects projection, not individual tool execution
    // or the production classes' Write declarations. No substitute can edit a model.
    internal abstract class RegistryTool(string name, bool write = false) : ITool
    {
        public string Name => name;
        public string Label => name;
        public string Description => "Offline registry fixture";
        public bool Write => write;
        public object ParametersSchema => new
        {
            type = "object", properties = new { fixture = new { type = "string" } }, required = new[] { "fixture" },
        };
        public object Execute(JsonElement args, ToolContext context) => throw new NotSupportedException("Registry fixture tools must never execute.");
    }
    internal sealed class SearchApiDocs() : RegistryTool("search_api_docs");
    internal sealed class ExecuteCsharp() : RegistryTool("execute_csharp", true);
    internal sealed class GetModelOverview() : RegistryTool("get_model_overview");
    internal sealed class GetElements() : RegistryTool("get_elements");
    internal sealed class GetElementTypes() : RegistryTool("get_element_types");
    internal sealed class GetElementDetails() : RegistryTool("get_element_details");
    internal sealed class ManageSelection() : RegistryTool("manage_selection");
    internal sealed class OpenView() : RegistryTool("open_view");
    internal sealed class SetParameters() : RegistryTool("set_parameters", true);
    internal sealed class CaptureView() : RegistryTool("capture_view");
    internal sealed class ExportDocuments() : RegistryTool("export_documents");
    internal sealed class GetModelHealth() : RegistryTool("get_model_health");
    internal sealed class GetLinkedModels() : RegistryTool("get_linked_models");
    internal sealed class GetLinkedElements() : RegistryTool("get_linked_elements");
    internal sealed class GetSchedules() : RegistryTool("get_schedules");
    internal sealed class GetElementRelationships() : RegistryTool("get_element_relationships");
    internal sealed class SummarizeElements() : RegistryTool("summarize_elements");
    internal sealed class ManageElementSets() : RegistryTool("manage_element_sets");
    internal sealed class TransformElements() : RegistryTool("transform_elements", true);
    internal sealed class DeleteElements() : RegistryTool("delete_elements", true);
    internal sealed class ChangeElementTypes() : RegistryTool("change_element_types", true);
    internal sealed class ManageViews() : RegistryTool("manage_views", true);
    internal sealed class ManageSheets() : RegistryTool("manage_sheets", true);
    internal sealed class ManageSheetPlacements() : RegistryTool("manage_sheet_placements", true);
    internal sealed class GetScheduleFields() : RegistryTool("get_schedule_fields");
    internal sealed class ManageSchedules() : RegistryTool("manage_schedules", true);
    internal sealed class CreateTags() : RegistryTool("create_tags", true);
    internal sealed class QuerySpatialElements() : RegistryTool("query_spatial_elements");
    internal sealed class MeasureGeometry() : RegistryTool("measure_geometry");
    internal sealed class GetModelCoordinates() : RegistryTool("get_model_coordinates");
    internal sealed class GetMepConnections() : RegistryTool("get_mep_connections");
    internal sealed class FutureWriteFixture() : RegistryTool("future_write_fixture", true);
}
