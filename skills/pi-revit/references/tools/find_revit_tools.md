# find_revit_tools

## Purpose and preconditions

Find PI-Revit capabilities by task wording, see what each tool does **not** cover and what to use instead, and activate relevant registered tools before calling them. Registration, activation, and reading guidance are separate steps; this tool does not run the discovered Revit operation or read the manual for you.

Use default `scope: "available"` for native utilities and the selected bridge's last-discovered tools. A tool registered earlier in this Pi session can remain registered after switching bridges while no longer appearing in that available catalogue. Use `scope: "documentation"` to find packaged guidance without contacting Revit or activating tools, including while Revit is closed. A documentation entry is not proof that the selected bridge supports a capability.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** no. **Effects:** session. **Requires an open document:** no.
- **Verify the outcome:** `none`: no durable outcome to check; report what was done.
- **Contract hash:** `25e8fc4224d36256`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `scope` | string | no |  | `available`, `documentation` |
| `query` | string | no |  |  |
| `names` | array of string | no |  |  |
| `activate` | boolean | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |

| Not covered by this tool | Use instead |
| --- | --- |
| Reading manual contents | Tool: read (open the returned path) |
<!-- generated:contract:end -->

## Public inputs and examples

`query` takes short English task words. Tool vocabulary is English; when the user writes in another language, translate the request into English search words first and still reply in the user's language. Matching ignores case, accents, simple plural and word forms, and numbers, which are call arguments. Words are matched against each tool's name, declared keywords, packaged summary, declared limits, input names and live description. Tools that match every word are returned first, followed by strong partial matches. If no tool matches every word, the response has `match: "partial"` or `"none"`. Use separate queries or `names` for different capabilities.

Find and activate a known specialist:

```json
{
  "names": ["manage_schedules"],
  "activate": true
}
```

Search by task wording, translated into English if the user wrote in another language:

```json
{ "query": "count walls per level" }
```

Find manuals for an explanation without connecting to Revit:

```json
{
  "scope": "documentation",
  "names": ["manage_schedules", "get_schedule_fields"],
  "limit": 20
}
```

Browse without activating:

```json
{ "offset": 0, "limit": 20 }
```

## Results and effects

The response reports `scope`, `match` (`all`, `partial`, `none`, `names` or `browse`), `bridge_catalog_known`, `bridge_catalog_observed_at`, paging fields (`total_count`, `offset`, `returned_count`, `next_offset`), `tools`, and `guidance`. A successful empty bridge catalogue is known empty; unknown means no successful discovery snapshot. Each tool entry gives its description, tier, effects and `source` (`native` or `bridge`), and these separate states:

- `registered`: the tool was registered in this Pi extension session. Bridge registrations can survive a switch even when the tool becomes inactive and the new bridge does not advertise it.
- `advertised_by_selected_bridge`: the selected bridge's last successful discovery snapshot advertises this tool. It is `null` before a successful snapshot and for native utilities; false can be meaningful after a known empty snapshot.
- `active`: the tool is exposed to the model. This does not prove current bridge support, a live connection, or a successful operation.
- `limits`: what the tool deliberately does not cover, each with an `alternative`. The kinds are `tool` (another public tool), `api` (Revit API members to verify with `search_api_docs` before custom execution), `user` (needs a user action or decision) or `revit_unsupported` (the Revit API itself does not offer it; the reference states the evidence). A tool from a newer bridge without declared limits reports them as unknown.
- `verification`: the minimum sufficient check of the tool's outcome (`reread`, `capture`, `inspect_output` or `none`).

When a query matches only partially or not at all, the result includes a `fallback` route. A missing dedicated tool is not evidence that the operation is impossible: verify the needed API members with `search_api_docs`, then use `execute_csharp` within the requested scope. Report "not possible" only after that check, naming what was checked. <!-- inv:limits-have-alternatives -->

`guidance` lists matching workflows, shared references and skills, including subject skills added to the package later, with absolute paths to read.

Each tool also carries a `documentation` object: `key`, absolute `path` when available, `status` (`available` or `missing`), `revision`, `package_version`, `packaged_contract_hash`, `live_contract_hash`, `observed_bridge_version` and `compatibility`. Missing, unreadable, non-file or outside-root manual targets are reported as missing without failing discovery; paths are resolved only inside the package. <!-- inv:manual-path-containment --> Compatibility compares the executable contract (input schema and declared effects) of the selected bridge's live tool with the contract its manual was generated from:

- `contract_match`: the manual was generated from this exact contract.
- `contract_changed`: the live contract differs. The active schema is authoritative; do not assume features the manual describes.
- `undocumented`: a newer bridge tool with no packaged manual.
- `unknown`: no live contract observed yet.
- `package_local`: a native utility shipped with this extension.

Wording changes to descriptions or guidelines do not change the contract.

Available-scope activation is additive: it preserves other Pi tools and activates the returned page, not every matching page. Partial pages default to five entries. Follow `next_offset` if more matches are needed. Newly activated schemas become available on the next model request; inspect them before constructing a call. Documentation scope only discovers references and leaves activation/connection state unchanged.

## Failures, recovery, and verification

Unknown exact names are rejected; check spelling, scope, and current bridge discovery. If a bridge tool is only in documentation scope, establish the intended running bridge and refresh via [ping](ping.md) before using it. If a selected instance becomes unavailable, use [manage_revit_instances](manage_revit_instances.md); the catalogue is not a live health check. Missing manuals do not establish tool unavailability, and manuals alone do not establish executability.

Verify the returned tool name, source, registration/activation, compatibility, and current schema. No document guard or `_operation_id` applies: discovery is not a model operation. Contract sources: `extensions/pi-revit/tool-catalog.ts`, `extensions/pi-revit/discovery.ts`, the generated contract snapshot and the documentation manifest. The inventory grows with the package; the current tool count is not an architectural limit.
