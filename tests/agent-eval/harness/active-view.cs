// The active view, so the runner can switch away from a view the agent created before deleting it.
return Emit(new { active_view_id = uidoc.ActiveView?.Id.Value });
