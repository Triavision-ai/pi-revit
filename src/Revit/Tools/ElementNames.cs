using Autodesk.Revit.DB;

namespace RevitBridge.Tools
{
    /// <summary>
    /// The one place a tool assigns a name or sheet number (inv:existing-objects-not-reused).
    /// A name another object of the same kind already uses is rejected before Revit is asked,
    /// with the existing object's identity in the failure, so the caller learns that the object
    /// predates this call instead of silently reusing or editing it. There is deliberately no
    /// "reuse" option: reusing someone else's object needs the user's decision, after which the
    /// caller works with that object's ID directly. The gate in check-tool-documentation.mjs
    /// rejects any other Name or SheetNumber assignment in a tool.
    /// </summary>
    internal static class ElementNames
    {
        public const string CollisionKey = "name_collision";

        /// <summary>Rename element, or fail with the colliding object's identity.</summary>
        public static void Assign(Element element, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must be nonempty.");
            if (element.Name == name) return;
            if (RequiresUniqueName(element) && FindSameKind(element, name) is { } existing) throw Collision(element, existing, "name", name);
            element.Name = name;
        }

        /// <summary>
        /// Kinds whose names Revit keeps unique. Sheets (identified by number), rooms and other
        /// objects may legitimately share a name, so they are not checked here.
        /// </summary>
        public static bool RequiresUniqueName(Element element) =>
            element is (View and not ViewSheet) or Level or Grid or ElementType or Material or ParameterFilterElement;

        /// <summary>Set a sheet number, or fail with the sheet that already has it.</summary>
        public static void AssignSheetNumber(ViewSheet sheet, string number)
        {
            if (string.IsNullOrWhiteSpace(number)) throw new ArgumentException("number must be nonempty.");
            if (sheet.SheetNumber == number) return;
            var existing = new FilteredElementCollector(sheet.Document).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(other => other.Id != sheet.Id && other.SheetNumber == number);
            if (existing != null) throw Collision(sheet, existing, "sheet number", number);
            sheet.SheetNumber = number;
        }

        /// <summary>Create a family type in a family document, or fail when the name is taken.</summary>
        public static FamilyType NewFamilyType(FamilyManager manager, string name)
        {
            EnsureFamilyTypeFree(manager, name);
            return manager.NewType(name);
        }

        /// <summary>Rename the current family type, or fail when another type has the name.</summary>
        public static void RenameCurrentFamilyType(FamilyManager manager, string name)
        {
            if (manager.CurrentType?.Name == name) return;
            EnsureFamilyTypeFree(manager, name);
            manager.RenameCurrentType(name);
        }

        /// <summary>Family types are not elements and have no ID; the collision names the type instead.</summary>
        private static void EnsureFamilyTypeFree(FamilyManager manager, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must be nonempty.");
            if (!manager.Types.Cast<FamilyType>().Any(type => type.Name == name)) return;
            var error = new ArgumentException(Message("family type", "name", name, null));
            error.Data[CollisionKey] = new Dictionary<string, object?> { ["existing_id"] = null, ["kind"] = "family type", ["name"] = name };
            throw error;
        }

        /// <summary>
        /// An object of the same kind with this exact name: same class, and for views the same
        /// view type (a floor plan and a ceiling plan may share a name). Null when the name is free.
        /// </summary>
        public static Element? FindSameKind(Element element, string name)
        {
            FilteredElementCollector collector;
            // Some API classes (for example Room) are not native filter classes; Revit's own
            // uniqueness check still applies to them when the name is assigned.
            try { collector = new FilteredElementCollector(element.Document).OfClass(element.GetType()); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { return null; }
            return collector.FirstOrDefault(other => other.Id != element.Id && other.Name == name
                && (element is not View view || (other is View otherView && otherView.ViewType == view.ViewType && otherView.IsTemplate == view.IsTemplate))
                && (element is not ElementType || other.Category?.Id == element.Category?.Id));
        }

        /// <summary>Pure message text, shared by every tool and tested offline.</summary>
        public static string Message(string kind, string what, string value, long? existingId) =>
            $"A {kind} with the {what} '{value}' already exists{(existingId is long id ? $" (id {id})" : "")}. Nothing was given that {what}. "
            + "That object existed before this call: do not edit, reuse, replace or delete it unless the user asks. "
            + "Ask the user, or choose a distinct value and report the collision.";

        private static ArgumentException Collision(Element target, Element existing, string what, string value)
        {
            string kind = target is View view ? $"{view.ViewType} view" : target.GetType().Name;
            var error = new ArgumentException(Message(kind, what, value, existing.Id.Value));
            error.Data[CollisionKey] = new Dictionary<string, object?>
            {
                ["existing_id"] = existing.Id.Value, ["existing_unique_id"] = existing.UniqueId, ["kind"] = kind, [what.Replace(' ', '_')] = value,
            };
            return error;
        }
    }
}
