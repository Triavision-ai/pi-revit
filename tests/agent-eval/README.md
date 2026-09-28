# Agent evaluation

Behavioral scenarios for real Pi sessions: does the agent find capabilities, respect
scope, verify outcomes, stop when done, and never declare an operation impossible
without checking? Offline checks cannot answer these questions; they need the real
agent against a real, disposable Revit model.

```powershell
$env:PI_CODING_AGENT_PATH = 'C:\path\to\node_modules\@earendil-works\pi-coding-agent'
node tests/agent-eval/run.mjs --fixture C:\path\fixture.json --out C:\path\outside-repo\eval-runs --scenarios inspect-empty-sheets,modify-perspective-view --repeat 3 --models provider/a,provider/b
```

`fixture.json` identifies the disposable model, and optionally the model to use:

```json
{ "title": "...", "document_id": "<project.documentId>", "file": "C:\\...\\copy.rvt", "disposable": true, "model": "provider/model-id",
  "values": { "sheet_number": "<sheet number>", "level_name": "<level name>" } }
```

**Safety.** The runner refuses to start unless the bridge's active document is exactly
the fixture (document ID *and* file path). The guard (`guard.ts`) permits only the
fixture's identity. It blocks saving, closing or opening documents, exports, printing,
process start and file deletion inside custom code, credential-file reads, and changes
to the user's script library. Sessions are ephemeral, and the model is passed per
invocation, so Pi settings are never changed. Traces are written outside the repository.

## Clean runs

Every run starts from the same fixture state, so earlier runs cannot contaminate later ones:

1. **Baseline.** The first call records `<out>/baseline.json` from the freshly opened fixture:
   the highest element ID, element and type counts, pinned elements and a signature of every
   view (name, template, display, crop, 3D camera and section box, hidden categories). It
   refuses a fixture that already has unsaved changes. Take it right after opening the
   disposable copy, and use a new `--out` directory after every reopen (the document ID changes).
2. **Reset before each run.** If a view the agent created is active, the harness activates
   the baseline view. It then deletes every element created after the baseline (element IDs
   grow monotonically) and restores pinned state. Resetting needs `"disposable": true` and
   never saves.
3. **Start-state check.** The fingerprint must equal the baseline. Otherwise the runner stops
   and names the differences (`reset.json`), so a contaminated run is never started or scored.
   Changes it cannot reverse, such as an edited baseline view, stop the round; reopen the copy.
4. **Setup.** A scenario's `setup` script creates its starting objects, for example a view
   with hidden content. The next reset removes them.
5. **Ground truth.** After the run, the harness records what the run created and which baseline
   views or pinned states it changed (`ground-truth.json`), and runs the scenario's `post_check`.

Several fixtures can be used: `fixture.json` may carry an `id` (for example `site` or `family`), and a scenario
names the fixture it needs with `fixture` (default: the project fixture); the runner skips the others. In a family
document the fingerprint also records every family type with its parameter values, the parameters and the current
type, and the reset removes family types and parameters added after the baseline. A scenario with
`expect_no_changes` is scored with `fixture_unchanged` although its policy allows writes.

The fingerprint does not cover every property: an edit to a baseline element's parameters is
not detected, and such scenarios must check it in their `post_check`. `--no-reset` disables
steps 1–5 and marks every run `start_state: "not reset"`.

Harness scripts live in `harness/`, prefixed with `common.cs`, and run through the bridge's
`execute_csharp` with the fixture identity. With `--models a,b` runs interleave
(A1 B1 A2 B2 …), so both models meet the same conditions.

## Scenarios and checks

**Scenarios** (`scenarios.json`) name the invariants they measure (`docs/invariants.json`).
Automated checks read the trace:

| Check | Pass condition |
| --- | --- |
| `final_answer` | The run ends with a final answer within its time limit. |
| `no_model_changes` | No write tool ran. Custom code is flagged for review. |
| `capability_checked` | Discovery, API search or a manual was consulted. |
| `api_checked_before_impossible` | Any "not possible" claim follows an API search. |
| `english_search` | Discovery queries were in English. |
| `reports_collision` | The answer says the requested name was already in use (English requests). |
| `reply_script` | A request written in a non-Latin script (Han, Arabic, Cyrillic, ...) is answered in that script. A Latin-script request, or an answer that is only a list of names, goes to review. No word lists. |
| `answer_mentions:<key>` | The answer contains a value, for example the colliding name a setup created. Language-neutral. |

Independent checks read the fixture after the run:

| Check | Pass condition |
| --- | --- |
| `fixture_unchanged` | Read and explain scenarios: nothing created, no baseline view or pinned state changed. |
| `baseline_views_unchanged` | Write scenarios: no baseline view was changed or deleted. |
| `ground_truth` | The scenario's `post_check` script passed, for example "a perspective view with that name, roofs visible, looking north-west". |

A scenario's `reference` script records facts to compare an inspection answer with, such as every placed
wall type's layers; it is stored in `ground-truth.json` and reviewed, not scored.

`summary.json` also records `api_lookups` (API search calls and the members they named),
`scope_notes`, `completion_checks`, `inherited_state_results` and the created objects.

Ground truth that no script covers, such as answer quality, is reviewed and recorded with
the run. A scenario with `requires` needs extra fixture setup and runs only with
`--allow-requires`.

Compare variants only with the same model, fixture copy, prompts and initial state,
repeated at least three times; see `docs/evaluation.md`. One run is an observation,
not a reliability claim.
