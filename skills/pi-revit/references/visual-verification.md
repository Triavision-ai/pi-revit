# Visual verification and exported evidence

Read when the task creates or changes sheets, drawings, views, tags, schedule layout, geometry placement, or another visible result, or requests graphical deliverables. General explanations and bare counts do not need this workflow. API success establishes execution status, not visual correctness.

## Capture the actual result

- Use [capture_view](tools/capture_view.md) or a requested [image export](tools/export_documents.md) of the actual committed result. `capture_view` returns a temporary PNG path, not image contents.
- Open that returned file with Pi's image-capable `read` tool and inspect its contents. Do not infer appearance from a filename, image dimensions, successful export, or numeric positions.
- Frame the real view before capturing. A tiny drawing surrounded by blank space is not sufficient evidence. Use a readable full-sheet image and close-ups when necessary. Place schedules on a sheet when a graphical check of their sheet layout is needed and that placement is within the task scope.
- Use before/after images when needed to demonstrate movement, rotation, placement changes, or isolation. Preserve useful evidence without repeatedly exporting unrelated views.

## Assess the requested outcome

Check that the requested objects are visible and correctly positioned, view extents are appropriate, labels and schedule contents are readable, and the composition has no unintended overlap or clipping. Check scale/template/titleblock requirements when part of the request. Model coordinates and successful placement calls alone do not establish a good drawing layout.

If a correction is within the user's task, make it using the same identity and transaction rules, then inspect the changed result. If evidence is unavailable or unreadable, state that visual verification is blocked or incomplete and identify what remains unchecked; do not label the visible work fully verified.

## Stop when the request is verified

Verification checks the request; it is not an open-ended improvement loop. Before capturing, list the request's explicit requirements. Then:

- Correct only defects that break one of those requirements, such as a requested object missing or unreadable, an overlap in a requested layout, or a wrong name or target.
- Once every requirement is verified, stop and report. Offer anything else you noticed (graphic styles, hidden clutter, detail level, framing) as a suggestion; do not do it unrequested.
- Do not hide, remove or crop away content the request asks to show. For example, a view of "the whole building" keeps its roofs.
- A result derived from an existing object, such as a duplicated view, copied element or reused type, inherits that object's state. Its result reports `inherited_state`, and a view made by a script appears in `model_changes.new_views` with its hidden categories and elements. Check that state against the request, not just the framing: an image can look complete while roofs or walls are hidden.
- If the same capture has been repeated after further edits and requirements still seem unmet, stop and report what is met, what is not, and why, rather than continuing to adjust. <!-- inv:completion-proportionate -->

The extension also notices repeated identical verification calls after further edits in one run, and asks for this check.

## Retain and deliver

Retain inspected pictures with the task's evidence and show or link them with concise captions describing what was actually verified. Do not fabricate an image as proof of Revit output. `capture_view` uses temporary files; preserve them in the task's permitted evidence location when long-term retention is needed.

Use export's returned `outputDir` and file paths as authoritative. The default model directory derives from saved/cloud identity or a session fallback, not just the title; Save As can change the destination. Do not reconstruct paths from `project.documentId` or trigger an unrelated export merely to discover a directory.

For PDF/DWG/PNG/IFC deliverables, inspect the resulting artifact as appropriate. Existence and file size do not establish drawing quality or IFC geometry/schema validity. Failed exports can leave partial files. Record meaningful limits and warnings. Evidence capture or export does not authorize saving the Revit model; honor the user's scope and save instructions.
