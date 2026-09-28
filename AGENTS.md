# Contributing to PI-Revit

This file guides agents changing **this source repository**. Start with
[the architecture](docs/architecture.md) for resource ownership and discovery,
and [evaluation](docs/evaluation.md) for evidence and validation limits.
Follow the user's requested scope; an investigation does not authorize implementation,
and a source change does not by itself authorize installation, deployment, publication,
or changes to a live Revit model.

## Two different AGENTS files

- **This file:** contributor instructions, repository structure, and checks.
- **[workspace/AGENTS.md](workspace/AGENTS.md):** template copied into the user's
  Revit working folder by setup. It owns model output locations and local session
  conventions. It is not the contributor guide or the complete tool manual.

Pi's global/project instruction scope is separate from resource type. A skill is
a task-specific entry with optional references; a tool is executable behavior;
a package distributes them. Do not turn every manual into a separate skill or
copy all operational guidance into workspace instructions.

## Where a change belongs

Fix a class of problem where every present and future resource inherits the fix: in a
shared mechanism, in declared metadata, or in the platform section. A sentence in one
manual is never the only fix. Each rule has one owner.

| Change | Primary owner | Update alongside it |
| --- | --- | --- |
| Revit operation, inputs, outputs, effects | `src/Revit/Tools/<Tool>.cs` | `ToolRegistry.cs`, manual, focused C# tests |
| A tool's contract: keywords, limits with alternatives, verification | the tool's `Keywords`/`Limits`/`Verification` (bridge) or `extensions/pi-revit/contracts.ts` (native) | `npm run generate:contracts`; discovery corpus entries |
| Parameter lookup by name, BuiltInParameter or GUID | `src/Revit/Tools/ParameterResolver.cs`, the only resolver | element-query tests; never `LookupParameter` in a tool |
| Special or system-owned objects (revision schedules, templates, groups, design options) | `src/Revit/Tools/ElementTraits.cs` | element-traits tests; flag or count, never mix silently |
| State an object inherits when created from an existing one (duplicate, copy, mirror, retype) | `src/Revit/Tools/InheritedState.cs` (reading) and `InheritedState.Summary.cs` (pure summary) | derived-state tests; the gate requires it wherever a tool duplicates, copies or retypes |
| Assigning a name or sheet number; name collisions | `src/Revit/Tools/ElementNames.cs`, the only place a tool assigns them | the gate rejects any other `Name`/`SheetNumber` assignment |
| What a call changed in the model (`model_changes`) | `src/Revit/Tools/ModelChanges.cs` and `ChangeSet.cs`, attached once by the dispatcher in `BridgeServer.cs` | derived-state tests; never report changes per tool |
| Shared document identity/transaction rules | `src/Revit/Tools/DocumentGuard.cs`, `ModelEditBatch.cs`, related helpers | guard/transaction tests; execution and recovery references |
| HTTP, queueing, receipt retention | `src/Revit/BridgeServer.cs`, `CommandQueue.cs`, `OperationStore.cs` | receipt/result tests and recovery reference |
| Cross-cutting protocol (capability, scope/completion, evidence, identity, language) | `extensions/pi-revit/platform-prompt.ts`, stated once for all tools | platform tests; never repeat it per tool or per manual |
| Completion/loop steering | `extensions/pi-revit/completion-monitor.ts` (metadata-driven) | platform tests; the `modify-*` evaluation scenarios |
| Objects that predate the request; per-request created-object ledger | `extensions/pi-revit/scope-monitor.ts` (reads `model_changes` and `name_collision`) | scope-monitor tests; the `modify-name-collision` scenario |
| Pi registration, result presentation, retries | `extensions/pi-revit/index.ts`, `tool-schema.ts` | extension tests and affected manuals |
| Instance routing | `extensions/pi-revit/instance-router.ts` | instance-router tests and instance manual |
| Discovery matching and ranking | `extensions/pi-revit/discovery.ts`, `tool-catalog.ts` | `tests/discovery/corpus.json` (recall gate) and catalogue tests |
| Documentation index, groups, guidance resources | `skills/pi-revit/tool-manifest.json` | regenerate; corpus entry for each new workflow |
| A "never" or "must" rule | `docs/invariants.json` plus an `<!-- inv:<id> -->` tag on the sentence | a code test, or, only for agent intent, an `agent-eval` scenario |
| Agent behavior worth measuring | `tests/agent-eval/scenarios.json` | invariants it protects; live runs on a disposable fixture |
| Reusable script library | `extensions/pi-revit/script-library.ts` | script-library tests and manual |
| Cross-tool operating rule | `skills/pi-revit/references/` | short entry link only if needed on every task |
| One public tool's usage | hand-written part of `skills/pi-revit/references/tools/<public_name>.md` | executable examples; the Contract block is generated |
| A multi-tool task recipe | workflow reference under `skills/pi-revit/references/` | manifest guidance entry, corpus entry and task-based evaluation |
| Revit subject knowledge | a scoped future `skills/revit-<subject>/SKILL.md` and references | official Autodesk sources and version; discovered automatically; add corpus and routing evaluation |
| API signatures | existing `search_api_docs` implementation and live version's documentation | search tests; do not maintain a parallel copied API catalogue |
| Installation or output-folder convention | `scripts/`, `bin/pi-revit.js`, `workspace/AGENTS.md` | README and installer tests |

Generated artifacts are never edited by hand: `skills/pi-revit/contracts.generated.json`,
each manual's `Contract (generated)` block and the tool-index tables. Change the code or
manifest and run `npm run generate:contracts`; `npm run test:docs` fails when they are stale.

Tool vocabulary is English. There are no per-language rules: the model translates a
request into English search words, replies in the user's language, and reads localized
Revit names from results. Prefer exact identities (BuiltInParameter, GUID) over display names.

The future subject library is an extension point, not an already implemented
library. Keep one package until independent ownership or releases justify another.
Names in the table are repository-relative paths, not files to create indiscriminately.

## Adding or changing a public tool

1. Decide whether it belongs in the Revit bridge or the Pi extension. A bridge
   `ITool` implements metadata/schema and execution, and is registered in
   `src/Revit/ToolRegistry.cs`. Set `Write`, `Effects`, `RequiresDocument` and tier to
   match real behavior. `write: false` does not mean no UI/file effects. Tools with
   `RequiresDocument: false` run off the API thread with no Revit context; do not
   access the Revit API there.
2. Declare the contract. `Keywords` holds at least 3 English task words, outcome words
   and synonyms. Every `Limits` entry names what the tool does not cover and an
   alternative: another `tool`, `api` members (checked against the installed
   RevitAPI.xml), a `user` action, or `revit_unsupported` with its evidence. A tool
   that writes or has effects declares `Verification`. Prompt guidelines hold only
   tool-specific facts; identity, manual location, capability and completion rules
   live once in the platform section.
3. Use the shared primitives: `ParameterResolver` for any parameter reference,
   `ElementTraits` for special objects, `ModelEditBatch` for edits, `InheritedState` for
   anything created from an existing object, and `ElementNames` for names and sheet numbers.
   The dispatcher reports `model_changes` for every write tool; do not add a private variant. Preserve enforced
   safeguards. The public bridge contract is the class schema plus registry-added
   `expected_document_id` and extension-added `_operation_id`. Never weaken identity
   guards or receipt routing to make an example pass.
4. Add the manifest entry (name, source, group, summary) and a manual under
   `references/tools/`. Run `npm run generate:contracts`, which writes the manual's
   Contract block and the tool index. Write the hand part against the final schema
   and actual execution: purpose/preconditions, action differences, effects/identity,
   units/coordinates, result interpretation, recovery and verification. Include at
   least one valid JSON input example and a source pointer. Label example IDs as
   placeholders to discover. A native tool also needs its `NATIVE_CONTRACTS` entry; the
   manifest's native entries reserve its name against bridge descriptors.
5. Add at least 5 English task phrasings to `tests/discovery/corpus.json`. Register
   any new "never" or "must" rule in `docs/invariants.json` with its test. Add or extend
   an `agent-eval` scenario when the tool changes what the agent can do.
6. Update `documentation_revision` whenever guidance changes. Compatibility with a
   bridge is the per-tool contract hash (input schema and effects, without wording).
   Do not bump the package release or redeploy unless part of the task.
7. Run the checks below. Include defaults, rejected inputs, state transitions,
   partial/rollback results and caller-visible outcomes where meaningful. Update the
   README/change log for user-visible behavior. Report live checks separately from
   offline checks.

## Maintaining skills and workflows

- Keep `skills/pi-revit/SKILL.md` a concise task router with essential cross-tool
  rules. Put detail in linked references. Large collections of tool names do not
  belong in its description. Tools/manuals can also be discovered without loading
  this skill; do not assume the model will always select it.
- Every workflow distinguishes explanation/planning, inspection, and modification
  or deliverable creation. Do not make an ordinary question open/edit/export a model.
- Add subject skills only for independently meaningful Revit tasks. Their references
  own modeling concepts, constraints and cited Autodesk Help knowledge, while tool
  manuals own our integration contract. Link between them; do not duplicate both.
- Put project-specific standards in the user's project context. Source-wide rules
  belong here, and runtime output conventions belong in the workspace template.
- Keep uncertainty explicit: a supported preview can validate then roll back;
  preview IDs are temporary; a timeout is not cancellation; a commit is not a save.
  Link to recovery/verification instructions instead of inventing another policy.

## Validation and completion

Run these from the repository root, using Node and .NET 8 SDK or newer. Point
`PI_CODING_AGENT_PATH` to an installed Pi package containing its `jiti` and
`typebox` dependencies when they are not locally resolvable. Current extension
fixtures require this variable; the path below is an example, not a fixed location.

```powershell
$env:PI_CODING_AGENT_PATH = 'C:\path\to\node_modules\@earendil-works\pi-coding-agent'
npm.cmd run generate:contracts
npm.cmd run test:docs
npm.cmd run test:extension
dotnet run --project tests/document-identity/document-identity-tests.csproj
dotnet run --project tests/element-query-regressions/element-query-regressions.csproj
dotnet run --project tests/element-traits/element-traits-tests.csproj
dotnet run --project tests/derived-state/derived-state-tests.csproj
git diff --check
```

`test:docs` checks actual registered input schemas and source-derived bridge
metadata without running Revit operations. It also checks that generated artifacts are
current, that contracts are valid with resolvable alternatives, and that the invariant
register and its tests agree. It enforces discovery-corpus recall, the startup prompt
budget and the architecture rules (no model saves, one parameter resolver, special
objects classified, inherited state reported for created-from-existing objects, names assigned
through `ElementNames`, model changes attached by the dispatcher). `test:extension` uses isolated mock
discovery and intercepted HTTP. Neither establishes live API behavior, successful
drawings, agent instruction adherence, or performance improvement.

Use the relevant existing suites for changed components: `tests/model-edit-batch`,
`transaction-export`, `operation-store`, `element-query-regressions`, `element-traits`, `derived-state`,
`linked-geometry`, `schedule-fields`, `search-engine`, and `installer` each document
their scope. Agent behavior is measured with `tests/agent-eval` on a disposable fixture
(see its README); it never runs as an incidental check. A full bridge build requires matching Revit SDK assemblies; build
using `scripts/build.ps1`, not deployment, when compilation alone is requested.
Do not install dependencies, publish a release, modify a live model or deploy an
add-in as an incidental documentation check.

Before completion review the diff, run appropriate checks, and explain what
changed, why, and what was actually tested. For model-affecting changes, include
live verification only when performed within the requested scope. See the
evaluation guide before claiming faster, cheaper, or more reliable agent behavior.
