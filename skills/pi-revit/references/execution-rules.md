# Shared execution rules

Read for tasks that interact with a Revit model. For tool-specific inputs and limits, use the [tool index](tool-index.md). These instructions explain existing behavior; identity checks, transaction handling, and retry protections are enforced in executable code.

## Scope and targeting

Classify the request as explanation/planning, inspection, or modification/delivery. Use only the required path. Read-only findings do not authorize repairs. Follow the user's established scope and model-saving instructions without adding an approval ceremony to already-authorized work. If the requested target or consequential action is unclear, resolve that ambiguity before the dependent action.

The first sole Revit instance can bind automatically. For multiple sessions, choose with [manage_revit_instances](tools/manage_revit_instances.md). Once selected, calls never silently fall back to another session. <!-- inv:no-session-fallback --> Selecting a bridge does not activate a document. Most bridge tools require an open document; `ping` and `search_api_docs` do not. A document-free API search still needs the bridge.

Read `get_model_overview` for the intended model before changing it. Copy `project.documentId` unchanged into `expected_document_id`. It identifies one open document in one bridge session, not a stable project ID, export-directory key, or title. Refresh after closing/reopening, restart, or switching instances. On rejection, establish that the intended model is active before obtaining its ID; never blindly replace the ID with that of another active model. <!-- inv:identity-refresh-intent -->

`get_model_overview` reports `project.documentKind`: `project` or `family`, and for a family its category, types and parameters. Each tool declares the document kinds it works in, and the bridge refuses a tool in any other kind before it runs, naming the route to use instead. In a family, types, parameters and formulas are edited through `FamilyManager` with `execute_csharp`. <!-- inv:document-kind-declared -->

The exact guard is required for all model writes and previews, `execute_csharp`, `export_documents`, `open_view`, and selection/zoom changes. <!-- inv:document-identity-guard --> `manage_sheet_placements` requires it even for `list`, because its metadata classifies the whole tool as write-capable. `manage_selection` requires it for `isolate_in_view: true`, including with action `get`. Pure reads can omit it unless their public schema requires it; supply it when an inspection must remain bound to one model. A supplied identity is checked. Legacy `expected_document` titles alone do not satisfy the guard.

## Discover before using

Use [find_revit_tools](tools/find_revit_tools.md) to activate relevant specialist tools. Read their current public schemas and focused manuals. Tool activation does not run a model operation or automatically read arbitrary Markdown. Prefer a dedicated tool over custom C# when it covers the requested operation.

Discover element, type, view, sheet, link, and field identities from results; examples do not provide usable project IDs. Linked identities remain in their linked document and must not be passed to host write or selection tools. <!-- inv:linked-ids-stay-linked --> The common selection sequence is `get_elements` → returned IDs → `manage_selection` with action `set`; selection has no inline query filter.

## Parameters and units

Display parameter names are localized. For missing display-name lookups or non-English documents, use discovered language-independent `BuiltInParameter` names, such as `ALL_MODEL_MARK` or `ALL_MODEL_INSTANCE_COMMENTS`, where supported. Parameter detail results expose `builtInParameter` for discovery. Shared parameters can use `guid:<GUID>` where the tool supports it. Distinguish missing values from empty values and ambiguous matches; never silently choose one duplicate display name. <!-- inv:parameter-ambiguity -->

Raw measurable parameter values and most returned coordinates use internal Revit units, with lengths in feet. `displayValue` is separately formatted for the document. Numeric query-filter and write inputs can instead use their explicit `unit` or document display units. Read the individual contract; do not apply one conversion rule indiscriminately. Sheet placement uses paper-space coordinates without multiplication by view scale; shared/model/view coordinates are distinct.

## Transactions and retained effects

Use preview to examine consequential or uncertain edits when supported. `preview` and `atomic` are independent: preview rolls back; atomic rejects the whole batch on a failed step. Default batches can retain partial success. A preview may exercise commit checks then roll back an enclosing group; check `commit_validation_performed` before claiming commit validation happened.

Read `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and tool-specific counts. `succeeded` represents committed steps, while `proposed` represents accepted steps rolled back by preview or atomic failure. Temporary IDs in proposals cannot be reused. Committed type changes can replace elements; follow the returned replacement identity. Per-step before/after values are not necessarily a final-model snapshot when the same parameter is written repeatedly or constraints affect other elements. Reread what matters.

Every call of a tool that can change the model reports `model_changes`: the objects it added, modified and deleted (`observed: false` means no document change was seen), and for new views their visibility state. <!-- inv:model-changes-reported --> Use it to state what a call changed, including custom scripts. An object made from an existing one inherits its state: duplicates, copies and retyped elements report `inherited_state` (hidden categories and elements, filters, overrides, template, carried Mark or Comments). Compare it with the request and say what the result derives from. <!-- inv:derived-state-reported -->

Objects that existed before the request are not yours. When the request asks you to create an object and one with that name already exists, or a creation fails with `name_collision`, do not edit, reuse, replace or delete the existing object; ask the user, or create under a distinct name and report the collision. <!-- inv:existing-objects-not-reused --> A PI-Revit scope note on a result means a call changed an object the request names that predates the request.

A commit is not a file save. <!-- inv:no-tool-saves-model --> Model rollback does not reverse prior UI actions, files, or external effects; custom scripts can have all of those. Export and model saving must be within the user's scope. Use returned export paths as authoritative rather than constructing paths from the document title or opaque ID.

For uncertain outcomes read [operation recovery](operation-recovery.md); for visible results read [visual verification](visual-verification.md). For oversized responses read [read_revit_result](tools/read_revit_result.md), while continuing to honor the original query's independent pagination and limits.
