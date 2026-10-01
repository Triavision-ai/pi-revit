# PI-Revit runtime architecture

PI-Revit connects Pi to Autodesk Revit through a local bridge. The package includes
the bridge source, the Pi extension, searchable tool contracts, operating guidance,
and the scripts needed to build and install it on Windows.

## Runtime layers

```text
Pi session
  ├── extension: discovery, session routing, results and operation receipts
  └── skill and manuals: task guidance and tool contracts
           │ authenticated localhost HTTP
           ▼
Revit bridge add-in
  ├── tool registry and input contracts
  ├── ExternalEvent queue for Revit API work
  ├── shared document and transaction safeguards
  └── tool implementations
           │
           ▼
Selected Revit document
```

The add-in is headless: it creates no ribbon or panels. Revit API work runs on
Revit's API thread through the queue. The bridge binds to loopback and creates a
new authentication token each time it starts. Discovery records live under
`%APPDATA%\RevitBridge`, including separate instance records and a legacy
`bridge.json`. These records belong to the local installation, not the package.

The HTTP bridge is local to the computer. Pi can send conversation context and
tool results to the selected model provider; local transport does not mean that
model information remains offline.

## Distributed resources

| Resource | Purpose |
| --- | --- |
| `src/Revit/` | Add-in source, tool implementations, registry, queue and operation store |
| `extensions/pi-revit/` | Pi tools, discovery, instance routing, result presentation and monitors |
| `skills/pi-revit/SKILL.md` | Concise task entry and links to relevant operating guidance |
| `skills/pi-revit/tool-manifest.json` | Tool summaries, groups and guidance index |
| `skills/pi-revit/contracts.generated.json` | Contract snapshot used for offline documentation discovery |
| `skills/pi-revit/references/` | Shared rules, recovery, visual verification, workflows and individual tool manuals |
| `scripts/` | Build, prerequisite checks, deployment, workspace setup, uninstall and diagnostics |
| `workspace/` | Generic workspace instructions and command templates |
| `bin/pi-revit.js` | Windows installer entry |

The manifest currently describes 30 bridge tools and six Pi utilities. The
extension can discover additional tools advertised by a selected bridge.

## Instructions, skills and tools

Resource scope and resource type are separate. Global or project scope determines
where Pi discovers a resource. Instructions, skills, tools, templates and packages
describe what it does. Installing a skill does not mean its complete body is always
loaded into context.

The extension injects a short platform protocol for capability checks, scope,
completion, evidence, document identity and language. Tool guidelines retain
tool-specific facts. A skill routes a task to the relevant manuals and workflows;
reading a manual remains a separate action. Important safeguards are enforced in
code because an agent may omit a skill or reference.

Tool vocabulary is English. The agent translates discovery terms, replies in the
user's language and reads localized Revit names from actual results. Exact
BuiltInParameter, BuiltInCategory and GUID identities avoid ambiguous display names.

## Discovery and contract compatibility

`find_revit_tools` has two scopes:

- **`available`** searches Pi utilities and the selected bridge's discovered
  catalogue. Query or exact-name results activate tools by default. Browsing alone
  does not activate them. Activation preserves other extensions' active tools.
- **`documentation`** searches packaged contracts and manuals, enriched with known
  bridge descriptors. It makes no bridge request and does not activate tools.
  A documentation entry does not establish executable support.

Search normalizes case, accents and word forms. It matches names, keywords,
summaries, limits, inputs and live descriptions. All-word matches rank first;
strong partial matches follow. A limit can lead to another tool, an API lookup,
a required user action, or a documented Revit limitation. No matching dedicated
tool does not by itself mean a task is impossible. Relevant workflows, shared
guides and sibling skills can also appear as guidance results.

Discovery reports registration, selected-bridge advertisement, activation,
verification and manual location. A discovery timestamp is a snapshot, not a health
check. `ping` and instance management establish connectivity. `ping` also reports
the package/guidance revisions and per-tool contract agreement.

The manual resolver accepts names from the packaged manifest, verifies local file
access and real-path containment, and never trusts a bridge-provided filesystem
path. A missing manual is reported without implying that the tool is unavailable.

Contract compatibility is exact and per tool. Its hash covers the input schema,
write/effects metadata and document requirement, excluding wording, titles and
examples:

| State | Meaning |
| --- | --- |
| `contract_match` | Packaged manual matches the observed bridge contract |
| `contract_changed` | The active schema takes priority over the packaged manual |
| `undocumented` | The bridge advertises a tool with no packaged manual |
| `unknown` | No live contract has been observed |
| `package_local` | A Pi-native utility |

Rewording guidance does not change compatibility. Changes to types, requiredness,
enumerations or effects do.

## Schemas and shared safeguards

For bridge tools, public inputs are composed in three stages:

1. The tool class declares operation inputs.
2. The registry adds exact document identity and requiredness.
3. The Pi extension adds operation-ID retry metadata.

Pi-native tools register TypeBox schemas and native contract metadata. Executable
code owns the contracts; packaged generated snapshots and manual Contract blocks
reflect them. Manuals also explain runtime conditions that JSON Schema cannot
fully express. Current schemas govern accepted inputs, and actual returned
outcomes govern claims of success.

Shared helpers resolve parameters, classify special objects, protect names and
sheet numbers, handle model-edit batches, and summarize inherited state. Parameter
resolution does not silently choose between same-named parameters. System-owned
objects, such as titleblock revision schedules, are distinguished from ordinary
editable elements. Each tool declares the document kinds it supports.

Read the intended document's overview and copy its `project.documentId` unchanged
to operations that require `expected_document_id`. This identifies an open
document in one bridge session; it is not a path, permanent project ID or
credential. Reopening the document or restarting the bridge invalidates it.
Instance selection and operation retries remain bound to their intended session.

Model-edit transactions, supported previews and partial/atomic outcomes are
reported explicitly. A model transaction does not undo earlier UI actions or file
output. A timeout is not cancellation, a commit is not a save, and unknown receipt
state is not proof that an edit never ran. See the recovery and execution manuals.

`execute_csharp` is an unrestricted CLR escape hatch. Its transaction handling does
not make arbitrary scripts a security sandbox. Verify unfamiliar API signatures
with `search_api_docs` and keep execution within the authorized task.

## Change reports and task monitors

The dispatcher reports `model_changes` for model-changing calls, including custom
scripts: added, modified and deleted objects, new-view visibility, and family
changes where applicable. Modified IDs can include regeneration-related events.
Objects derived from existing objects also report `inherited_state`, such as
hidden content, filters, overrides, templates and copied values.

The scope monitor tracks objects created in the current request. A change to a
pre-existing object named in the request, or a name collision, can append a scope
note. The platform protocol asks the agent to respect scope and disclose relevant
effects. The monitor steers; it does not block execution.

The completion monitor recognizes repeated verification after further changes.
It asks the agent to compare the result with the explicit requirements, verify,
then stop and report. Declared effects are a fallback for older bridges that lack
change reports. Legitimate multi-step tasks can continue.

Explanation, inspection and modification remain distinct task paths. A question
does not authorize editing; an audit does not authorize repairs or exports. Visible
results require the verification appropriate to the task, rather than a successful
API return alone.

## Local files and installation

Installation builds the bridge against the matching local Revit API assemblies:
.NET 8 for Revit 2025/2026 and .NET 10 for Revit 2027. Detection supports standard
Autodesk locations and explicit path overrides. Source is distributed because the
add-in is built locally. The installed Pi package and deployed add-in should use
matching versions.

Workspace setup creates a generic `Documents\pi-revit` folder by default and
preserves an existing workspace `AGENTS.md`. Updated output/safety conventions can
be merged from the packaged template when upgrading.

Default exports are sorted automatically under the exported document's
identity-derived `Models/<title>--<hash>/exports` directory. Model markers can
contain original paths or cloud identities. Captures, large result files,
reusable script definitions and run history also belong to the local user.
Keep these files outside source control; sanitizing a filename does not sanitize
the model information in its contents.

For operational details, start with [the skill](../skills/pi-revit/SKILL.md),
[execution rules](../skills/pi-revit/references/execution-rules.md),
[operation recovery](../skills/pi-revit/references/operation-recovery.md), and
[visual verification](../skills/pi-revit/references/visual-verification.md).
