# Native stair creation

Use this workflow for requested native stairs. Read [execution rules](execution-rules.md), discover dedicated capabilities first, and verify required API members with `search_api_docs` against the selected Revit version. The routes below describe Autodesk API capabilities; they are not a claim that every arrangement has been tested through PI-Revit.

## Choose the arrangement

| Requested arrangement | API route |
| --- | --- |
| Straight flight | `StairsRun.CreateStraightRun` |
| L-shaped, U-shaped or switchback with landings | Separate runs joined by automatic or sketched landings |
| Circular or spiral flight | `StairsRun.CreateSpiralRun`, with radius, angle and direction |
| Custom flight | `StairsRun.CreateSketchedRun`, with boundaries, risers and path |
| L-shaped or U-shaped winder | Construct sketched winder geometry; Autodesk's WinderStairs SDK sample demonstrates layout algorithms |
| Multiple stories | Create the base stair, then use `MultistoryStairs.Create`, `CanConnectLevel` and `ConnectLevels` |

Sources: Autodesk's [Creating and Editing Stairs](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Revit_Geometric_Elements/Stairs_and_Railings/Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Stairs_and_Railings_Creating_and_Editing_Stairs_html.html) and [WinderStairs SDK overview, page 8](https://static.au-uw2-prd.autodesk.com/DV2010_handout_2010_dv2010_advanced_revit_2014_api_handout.pdf). The SDK example is historical: verify current signatures and adapt its geometry, rather than copying its event handlers into a one-shot script. A sketched winder should not be described as having the same adjustable controls as a UI-created winder.

## Read the actual model and fit the design

Discover the intended base/top levels, their elevations and offsets, suitable stair/run/landing/support types, existing stairs, and the available footprint. Distinguish source dimensions from assumptions. Coordinate start/end connections, landing elevations, headroom and floor openings. Do not infer a usable stair solely from successful creation or a riser count.

### Project and language independence

- Discover and validate IDs in the currently bound document on every new task. Never copy level, stair, run, type or view IDs from examples, previous projects or test fixtures. Pass discovered identities and user dimensions as structured `inputs` when scripting.
- Select compatible types through API classes, construction enums and properties. A translated type name is a display label, not a portable selector. Use built-in parameter identities or shared GUIDs rather than English or other localized parameter names. If suitable content is missing, identify the missing type or family instead of silently substituting unrelated content.
- Derive base/top elevations, offsets and every story height from the requested model levels. Do not assume a zero base, equally spaced stories, a particular number of levels, or fixed riser counts. A five-level demonstration is not a default for future projects.
- Read the declared units of each input and convert with `UnitUtils`; raw Revit lengths remain internal feet regardless of the project's display units or country. Keep JSON measurements numeric; do not parse localized display strings as invariant numbers or interpolate them into C#.
- Obtain width, rise, tread depth, headroom, landing and railing criteria from the user's brief and applicable project requirements. A template's default or minimum is not a universal design standard. Dimensions used in a demonstration do not become country-wide defaults.
- Check supported Revit version, editable project-document context and installed API/content capabilities. Family documents, read-only linked models and unsupported APIs need their own supported route; project-independent tooling does not remove those Revit restrictions.

Arrangement and construction style are separate choices. Revit supports assembled, cast-in-place and precast stair construction. Choose an appropriate existing `StairsType`; `ConstructionMethod` is read-only. Duplicate types before changing shared material, tread, riser or support settings when existing stairs must retain their appearance. Open risers and timber/metal/concrete finishes are type/component choices, not additional run factories.

Sources: Autodesk [stair construction families](https://help.autodesk.com/cloudhelp/2016/ENU/Revit-Model/files/GUID-E36AE889-D507-4288-90BE-FCE2F120C2B6.htm), [ConstructionMethod API property](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/8d70d0f6-01f8-cc39-0134-71cf1392d0cd.htm), and the learning tutorial [Changing and editing stair types](https://www.autodesk.com/learn/ondemand/curated/stairs-and-railings-layout-and-design-in-revit/5RztCgRw21D1n5jef9JeNn). Check the installed version before relying on these reference signatures.

## Execute with the correct transaction boundary

Use direct `execute_csharp` with `transaction_mode: "group"`. Follow the [native edit-scope pattern](tools/execute_csharp.md#native-edit-scope-pattern): start `StairsEditScope` while no child `Transaction` is open, create runs/landings inside child transactions, check their commit statuses, then call `scope.Commit(failures)` and verify the scope finished. Finish every scope before returning. The backend assimilates the outer group only after success. Saved script runs currently use automatic transaction mode and cannot run this pattern.

Do not force-close another caller's transaction. If a child operation fails or is cancelled, throw and let the enclosing group roll back; do not return partial success. On timeout, recover the original operation receipt before any new creation request.

## Geometry details that matter

- Use internal feet and a deliberate coordinate frame. Do not equate a level's absolute elevation with a relative run elevation.
- Sketched runs take a base elevation relative to the stair base. Supply two separate connected boundary chains, risers spanning those boundaries, and a single path crossing every riser. The documented boundary curve types are lines and arcs. Check geometry constraints for the installed overload.
- Spiral runs use a center whose Z is in model coordinates. Check radius, width, direction, included angle and the resulting risers together; do not assume any angle/radius combination is valid.
- Divide the total rise across flights and derive landing elevations consistently. Check automatic-landing eligibility before creating one; its result can contain more than one landing. Use a closed sketched landing boundary when the automatic route cannot express the intended shape.

Sources: Autodesk [CreateSketchedRun](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/bb782d95-4792-8335-401c-97170542b96b.htm), [CreateSpiralRun](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/2f31d042-a202-a1a4-42fe-998b02e26a4d.htm), and the creation guide above. These 2026 reference pages do not replace installed-version API discovery.

## Finish the assembly and verify

After the stair scope is committed, inspect its associated railings. The API creates defaults with a new stair; explicit stair-hosted `Railing.Create` must run outside the stairs edit session and requires stairs without existing railings. Preserve pre-existing railings unless their replacement is in scope. For multistory stairs, connect only the requested eligible levels. Equal-height instances may be grouped; re-pinning a separately edited instance can overwrite its variations. See the creation guide above.

Create or adapt floor/shaft openings separately when requested. A stair does not establish a clear opening merely by intersecting a floor. Autodesk's [landings and holes tutorial](https://www.autodesk.com/learn/ondemand/curated/stairs-and-railings-layout-and-design-in-revit/1AWM0EpaXP4zTnNonnECGO) demonstrates the separate opening step.

Reread the native stair, runs, landings, level connections, actual/desired risers and associated railings. Check the affected geometry for continuity, fit, headroom and unintended host changes. Capture and inspect a useful native view under [visual verification](visual-verification.md). Return compact element identities, assumptions and remaining exceptions. Successful model creation does not establish construction-code compliance and does not save the model.
