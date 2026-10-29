# Shared execution rules

Read for tasks that interact with a Revit model. For tool-specific inputs and limits, use the [tool index](tool-index.md). These instructions explain existing behavior; identity checks, transaction handling, and retry protections are enforced in executable code.

## Scope and targeting

Classify the request as explanation/planning, inspection, or modification/delivery. Use only the required path. Read-only findings do not authorize repairs. Follow the user's established scope and model-saving instructions without adding an approval ceremony to already-authorized work. If the requested target or consequential action is unclear, resolve that ambiguity before the dependent action.

The first sole Revit instance can bind automatically. For multiple sessions, choose with [manage_revit_instances](tools/manage_revit_instances.md). Once selected, calls never silently fall back to another session. <!-- inv:no-session-fallback --> Selecting a bridge does not activate a document. Most bridge tools require an open document; `ping` and `search_api_docs` do not. A document-free API search still needs the bridge.

Read `get_model_overview` for the intended model before changing it. pi-revit binds every following model change, preview, export, view activation and selection change to the model of the latest overview; you never pass an identity yourself. The binding identifies one open document in one Revit session, not a project or a title: closing and reopening the document, restarting Revit or switching instances ends it. When Revit refuses a call because another model is active, read the overview again; if it now shows another model than the one the user means, ask the user instead of continuing on it. <!-- inv:identity-refresh-intent -->

`get_model_overview` reports `project.documentKind`: `project` or `family`, and for a family its category, types and parameters. Each tool declares the document kinds it works in, and the bridge refuses a tool in any other kind before it runs, naming the route to use instead. In a family, types, parameters and formulas are edited through `FamilyManager` with `execute_csharp`. <!-- inv:document-kind-declared -->

The binding is checked for all model writes and previews, `execute_csharp`, `export_documents`, `open_view`, selection/zoom changes and every `manage_sheet_placements` action, including `list`. <!-- inv:document-identity-guard --> Pure reads act on the active model.

## Discover before using

Use [find_revit_tools](tools/find_revit_tools.md) to activate relevant specialist tools. Read their current public schemas and focused manuals. Tool activation does not run a model operation or automatically read arbitrary Markdown. Prefer a dedicated tool over custom C# when it covers the requested operation.

Discover element, type, view, sheet, link, and field identities from results; examples do not provide usable project IDs. Linked identities remain in their linked document and must not be passed to host write or selection tools. <!-- inv:linked-ids-stay-linked --> The common selection sequence is `get_elements` → returned IDs → `manage_selection` with action `set`; selection has no inline query filter.

## Parameters and units

Display parameter names are localized. For missing display-name lookups or non-English documents, use discovered language-independent `BuiltInParameter` names, such as `ALL_MODEL_MARK` or `ALL_MODEL_INSTANCE_COMMENTS`, where supported. Parameter detail results expose `builtInParameter` for discovery. Shared parameters can use `guid:<GUID>` where the tool supports it. Distinguish missing values from empty values and ambiguous matches; never silently choose one duplicate display name. <!-- inv:parameter-ambiguity -->

Raw measurable parameter values and most returned coordinates use internal Revit units, with lengths in feet. `displayValue` is separately formatted for the document. Numeric query-filter and write inputs can instead use their explicit `unit` or document display units. Read the individual contract; do not apply one conversion rule indiscriminately. Sheet placement uses paper-space coordinates without multiplication by view scale; shared/model/view coordinates are distinct.

Shared workflows and reusable scripts must obtain model identities, content and design inputs from the current task. Do not embed test-fixture IDs, private project paths, translated type-name selectors or demonstration dimensions as global defaults. Keep structured numeric inputs separate from localized display text and C# source. Choose country/project requirements explicitly; successful Revit execution alone does not establish compliance with them.

## Transactions and retained effects

`execute_csharp` defaults to one automatic transaction. Direct calls may opt into `transaction_mode: "group"` for native edit scopes such as stairs: the script owns child transactions inside a backend group. All scopes must finish before returning. Any recorded native error or observed child rollback aborts the group; intentional partial rollback is unsupported. Group rollback covers only the target document. Saved script runs remain automatic.

Commit-time diagnostics supplement the existing outcome fields. Model-only edit batches may return `commitDiagnostics`, one entry per attempted commit; scripts may return a single diagnostic object when native warnings occur. Each report includes `transaction_status`, a bounded `failures` list, and `failures_truncated`. Rows include a stable `failure_definition_id` GUID when available, `severity`, the localized `description`, raw `failing_element_ids` and `additional_element_ids`, plus `element_ids_truncated` and `metadata_incomplete`. Limits are eight failures, twelve IDs in each ID list, and 320 description characters. These report bounds do not reduce the full attribution used internally to isolate rejected batch edits. Use GUIDs and IDs rather than parsing localized text. Missing metadata is explicit. Commit exceptions carry the same diagnostic information in their error text. The reported transaction status describes that commit attempt; a preview's enclosing group can subsequently roll it back, so `committed`/`proposed` remain authoritative for retained effects.

Use preview to examine consequential or uncertain edits when supported. `preview` and `atomic` are independent: preview rolls back; atomic rejects the whole batch on a failed step. Default batches can retain partial success. When Revit rejects a batch at commit (for example a group member edited outside group edit mode), the updates on the elements Revit names fail with its reason and `named_by_revit: true`; a non-atomic batch then commits the remaining updates once more, while an atomic batch, or a rejection that names none of the updates, reports every update as failed. Nothing is retried beyond that. A preview may exercise commit checks then roll back an enclosing group; check `commit_validation_performed` before claiming commit validation happened.

Read `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and tool-specific counts. `succeeded` represents committed steps, while `proposed` represents accepted steps rolled back by preview or atomic failure. Temporary IDs in proposals cannot be reused. Committed type changes can replace elements; follow the returned replacement identity. Per-step before/after values are not necessarily a final-model snapshot when the same parameter is written repeatedly or constraints affect other elements. Reread what matters.

Every call of a tool that can change the model reports `model_changes`: the objects it added, modified and deleted (`observed: false` means no document change was seen), and for new views their visibility state. <!-- inv:model-changes-reported --> Use it to state what a call changed, including custom scripts. An object made from an existing one inherits its state: duplicates, copies and retyped elements report `inherited_state` (hidden categories and elements, filters, overrides, template, carried Mark or Comments). Compare it with the request and say what the result derives from. <!-- inv:derived-state-reported -->

Objects that existed before the request are not yours. When the request asks you to create an object and one with that name already exists, or a creation fails with `name_collision`, do not edit, reuse, replace or delete the existing object; ask the user, or create under a distinct name and report the collision. <!-- inv:existing-objects-not-reused --> A PI-Revit scope note on a result means a call changed an object the request names that predates the request.

A commit is not a file save. <!-- inv:no-tool-saves-model --> Model rollback does not reverse prior UI actions, files, or external effects; custom scripts can have all of those. Export and model saving must be within the user's scope. Use returned export paths as authoritative rather than constructing paths from the document title or opaque ID.

For uncertain outcomes read [operation recovery](operation-recovery.md); for visible results read [visual verification](visual-verification.md). For oversized responses read [read_revit_result](tools/read_revit_result.md), while continuing to honor the original query's independent pagination and limits.
