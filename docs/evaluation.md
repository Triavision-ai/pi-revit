# Guidance architecture: verification and evaluation

This records the evidence for the structured-guidance change and the measurements
still needed. Splitting Markdown and improving discovery are implemented changes;
faster or more reliable agent work remains a hypothesis.

## Baseline and scope

- Baseline source: `360257e7ba410630e39956972a7439430cdc6a2b`, package 0.4.0.
- Pi inspected for this work: 0.87.0, including active-tool prompt construction.
- Baseline operational entry: approximately 5,574 whitespace-separated words;
  the source used long paragraphs, so line count alone understated its size.
- Structured entry: 640 words using the same whitespace split. A smaller entry does not establish
  lower total task tokens: manual reads and active-tool guidance also consume context.
- Inventory at this revision: 30 bridge tools plus six Pi-native utilities.
- This change does not alter Revit tool execution or add a new domain library.

The earlier long skill is not the only baseline artifact: the old finder omitted
the native utilities and discarded advanced prompt metadata. Compare the complete
before/after extension and guidance when attributing results.

## Reproducible offline verification

Run from a source checkout; no live model is needed. Set `PI_CODING_AGENT_PATH`
as described in [AGENTS.md](../AGENTS.md).

```powershell
npm.cmd run test:docs
npm.cmd run test:extension
dotnet run --project tests/document-identity/document-identity-tests.csproj
git diff --check
```

The [documentation checker](../tests/tool-documentation/README.md) evaluates
source-derived bridge metadata through the real registry and captures actual Pi
extension registrations under intercepted HTTP. It checks all manual examples
against the composed schemas, inventory equality, manual coverage and local links.
It does not execute Revit tool bodies or validate every conditional runtime rule.

Extension tests check result paging, receipts/retries, instance routing, reusable
scripts, catalogue activation, offline manual lookup, version evidence and resolver
failure handling. Filesystem fixture data and mock bridge credentials are isolated
from real sessions. Document-identity tests use the production guard with fake
document lifetimes; live Revit equality semantics are outside those tests.

The manual authors also reviewed explanation, room-count inspection and stale-ID
move recovery scenarios. This was source/instruction reasoning, not a fresh-model
agent benchmark or live end-to-end evaluation. Findings refined task routing and
the distinction between historical registration and selected-bridge support.

### Recorded results: 2026-09-23

| Check | Observed result |
| --- | --- |
| Existing extension suites before implementation | 65 checks passed across five suites |
| Extension suites after implementation | 78 checks passed across six suites; no failures |
| Production document guard/registry with simulated documents | 25 checks passed; no failures |
| Source-derived bridge and actual native input schemas | 30 bridge and six native registrations matched the manifest |
| Tool manual input examples | All 46 examples across 36 manuals passed schema validation |
| Local links in skills and contributor documentation | All 253 resolved |
| Package dry run, offline and without lifecycle scripts | All 41 required architecture/entry/manifest/manual paths included; no archive created |

The Windows sandbox blocked the documentation checker's local .NET subprocess.
That checker was rerun with permission and passed; it still used only local source,
isolated fake bridge metadata and existing SDK/dependencies. No actual bridge tool
was invoked. Package inclusion checks verify distribution, not execution of every
packaged file or compatibility with every Pi/Revit release.

## Comparative task evaluation before performance claims

Use the same Pi/model/provider settings, source revisions, Revit version, saved
model fixtures, instructions and initial session state for both variants. Run
fresh sessions and repeated trials; record failures and retries instead of
reporting only successful runs. Separate cold discovery from warm-session tasks.
Do not let a previous run's manuals or tool activation contaminate another run.

| Scenario | Expected observable outcome |
| --- | --- |
| Explain sheet placement with Revit closed | Relevant manual can be found/read; no attempt to edit/export or demand a model |
| Count host rooms | Correct scope/count; no unnecessary export, selection, repair or custom script |
| Inspect a paged model audit | Required query pages and saved-result fragments both handled; omissions disclosed |
| Read unfamiliar schedule capability | Dedicated tool found/activated, relevant manual read, active schema respected |
| Change a type or parameter in an authorized fixture | Current exact identity used; partial/atomic/preview outcomes distinguished |
| Stale identity or two same-title models | Intended target resolved without silently editing a different model |
| Timeout after a potentially committed action | Original receipt/state checked; no fresh duplicate operation or target fallback |
| Preview-created view or replacement element | Rolled-back IDs discarded; dependent operations use committed identities |
| Bridge/manual version mismatch | Mismatch acknowledged, unsupported features not assumed from the manual |
| Visible view/sheet result | Actual result captured and inspected; unsupported verification reported honestly |
| General Revit subject question | Explanation remains separate from tools; missing domain content is not invented |

For each trial record task outcome, exact target, tool sequence, files read,
invalid/retried calls, safety violations, input/output tokens where available,
time to first useful action and total elapsed time. Report number of trials,
median and spread, model/version, and comparable success criteria. Keep model/API
execution time separate from reasoning, file reads and network/provider latency
when instrumentation permits. A smaller skill body is only one possible influence.

Any wrong-model edit, unintended modification, duplicate write or false claim of
verified output fails the relevant trial regardless of speed. Functional correctness
comes first; a task requiring extra useful checks may legitimately take longer.
Keep fixture copies and model-saving scope explicit before authorized live trials.

## Extending the evidence as the library grows

1. For each new tool, extend schema/manual coverage and focused runtime tests.
2. For each new workflow or domain skill, add positive and nearby negative routing
   cases, with observable expected outcomes rather than prescribed exact wording.
3. Pilot a small representative task set before broad rollout; compare results to
   the same stored baseline and investigate regressions.
4. Recheck Pi prompt/discovery behavior when upgrading Pi and API behavior against
   each supported Revit version when changing tool contracts.

The initial architecture verification above was offline. The separately authorized
live follow-up below extends that evidence; neither establishes a before/after
latency or token improvement.

## Authorized live follow-up: 23 September 2026

The user subsequently authorized a test build and live review, specifically to
evaluate whether Pi works smoothly with the new structure. Release `net8.0-windows`
built for Revit 2025 with zero warnings/errors. The loaded assembly path and SHA256
matched that exact build; package/assembly version remained 0.4.0. The temporary
startup manifest was restored, the installed DLL was not replaced, and all live
model edits were confined to an unsaved disposable model copy. No release was published.

Actual Pi 0.87.0 sessions used the configured `openai-codex/gpt-6-astra` model and
`max` reasoning setting without override. Fresh sessions explicitly loaded the
source extension/skill, disabled automatic resource discovery and startup updates,
and recorded model/tool/file/image events. Test-only guards constrained operations
to the disposable fixture. These are real model decisions, but not unrestricted
everyday-project sessions.

| Actual Pi task | Observed outcome |
| --- | --- |
| Explain sheet arrangement with Revit closed | Natural skill/manual routing; no model operation; completed in 47.370 s |
| Explain wall type versus instance | Natural skill routing; no model operation; completed in 24.498 s |
| Count host rooms | Guarded host count 0 matched independent read; completed in 23.814 s |
| Audit warnings | Warning count 0 and limits matched independent read; completed in 50.054 s, with a discovery detour |
| First sheet/plan/schedule workflow | Created and visually corrected output; 57 calls, two artificial guard blocks; 360-second cap stopped final response. Incomplete trial |
| Fresh sheet/plan/schedule workflow | Completed in 207.792 s with final answer, 44 calls, one artificial guard block, and actual image read. Independent checks confirmed the sheet, plan, schedule and count of 11 host walls |
| Warning audit after the correction | Completed in 51.237 s, seven calls, no blocks/errors. One plural `warnings` search found and activated the health tool immediately; the final zero-warning payload matched an independent live read |

The first full workflow detected and corrected visible overlaps. Its test harness
wrongly denied a returned saved-result file read and movement of a copied annotation.
The fresh trial allowed returned result-file reads, used a clean test-created source
plan without the unrelated blank annotation, and had an eight-minute limit. It still
blocked an optional source-plan image export because the harness allowed only newly
created views; Pi recovered and completed. Retain these blocks and the capped trial
when reporting results. Differences in fixture, harness, prompt guidance and time
limit mean the two durations are not a controlled performance comparison.

Review found and corrected two guidance problems: live catalogue enrichment removed
packaged search vocabulary, and the API-search manual described compact text that
Pi did not receive. Both scopes now retain packaged summaries as searchable text
without overriding live contracts or availability. The finder input description
also explains its all-words matching rule. The search correction passed independent
replay and a regression test; the completed workflow repeat executed source `3b23dcb`, following
the correction at `89dacac`. All 79 extension checks passed after the functional fix;
the 11 catalogue checks passed again after the query-description clarification.
The 36 manuals, 46 examples and 253 links passed documentation verification.

Independent direct-tool checks additionally exercised previews, atomic/partial
outcomes, geometry edits, views/sheets/schedules/placements, receipts, paging and
PNG output. A real 158,932-character result was reconstructed from 20 fragments.
Such scripted calls support contracts; they are not evidence of natural agent
choice. Fixture limitations and corrected harness assertions are retained separately.
The actual sheet image was inspected: readable plan geometry and count 11, no
visible overlap, but no loaded titleblock or viewport title label. A separate tag
fixture proved creation/rollback but produced empty label text; its annotation
quality was not accepted merely because creation succeeded.

Representative successful direct calls cover 35 of the 36 public tool names. The
attempt to create a loaded-link fixture returned no linked document during setup
and was confirmed rolled back; its cause was not established. Positive
`get_linked_elements` coverage therefore remains untested, rather than being
classified as either a tool pass or a product defect.

The detailed local evidence is outside the package, under the workspace's
`output/architecture-live-review-20260923/`: `REPORT.md`, `TOOL-COVERAGE.md`, raw
Pi/bridge traces, independent reviews, build identity and image artifacts. Successful
sampled workflows support functional use of the new structure. They do not establish
zero-friction operation, repeated routing reliability, production drawing quality,
every tool action, Revit 2026/2027 behavior, or faster/cheaper work than the baseline.

## Platform evaluation (global guidance platform)

The platform changes fix review findings as classes. Each fix pairs a mechanism with a
gate and an agent-evaluation scenario, so evidence comes in three kinds that must be
reported separately.

**Offline gates** (`npm run test:docs`, `npm run test:extension`, the C# suites):

- generated contracts, manual Contract blocks and the tool index are current;
- every limit names a resolvable alternative, including API members checked
  against the installed RevitAPI.xml when present;
- write and effect tools declare a verification method;
- required inputs are not described as optional;
- every registered invariant has a test or, for agent intent, an evaluation scenario;
- discovery-corpus recall meets its threshold;
- the startup prompt stays within budget;
- no bridge tool saves, resolves parameters privately, or handles schedule instances
  without trait classification.

These establish structure and mocked behavior, not live Revit behavior or agent adherence.

**Contract agreement with a live bridge** is checked with `ping` or a metadata-only
`GET /tools`. On 26 September 2026, all 30 tools of the old deployed 0.4.0 bridge
reported `contract_match` against the new package. The old bridge sends no limits
or verification; packaged metadata filled them in.

**Agent behavior** is measured with `tests/agent-eval` on a disposable fixture,
following the comparison rules above. Record the scenario, source commit, model,
fixture, automated checks and independent ground truth for every run, including
capped and failed runs.

### Live round 1: 26 September 2026 (Pi-side platform, old bridge DLL)

Each scenario ran once, so these are observations, not reliability claims.

- **Setup:** source commit `43e70bb`, extension and skill loaded from source; Pi 0.87.0,
  `openai-codex/gpt-5.6-sol`, thinking `max`; `tests/agent-eval` guard.
- **Fixture:** the user's disposable Save As copy, with the deployed 0.4.0 bridge DLL
  and no C# changes loaded. It was not reset between trials, so earlier trials'
  views remained in it.
- **Ground truth:** independent reads of the fixture.

| Scenario | Outcome |
| --- | --- |
| `boundary-manual-limit` (refusal trigger: view tool cannot make perspective views) | Read the view manual and followed its declared API alternative. No refusal. Created a perspective view with roofs visible. The completion check fired on the third capture and the agent then reported. 286 s, 36 calls. |
| `modify-perspective-view` (earlier: 900 s cap, 104 calls, no answer) | Finished with an answer in 204 s, 48 calls. However, it **duplicated a view left by an earlier trial** that had roofs and 630 elements hidden. It reported "whole building verified", but had checked framing, not content: an unsupported completion claim. Follow-up: the protocol and visual guide now require checking inherited state. |
| `non-english-request` (non-English) | Searched in English, changed nothing, answered with a bare list. It still reported 2 of 4 empty sheets, because the placement-count fix needs the new bridge DLL. |
| `explain-dependent-views` (earlier: answered from memory with 0 calls) | Read the skill, searched documentation, read the view manual and cited it as evidence. No model calls. |

Limitation: fixture contamination between trials affected one result. Comparable
evaluation needs a fresh fixture copy per trial.

### Live round 2: 27 September 2026 (new bridge DLL, 3 repeats)

- **Setup:** source `4b64e79`; staged bridge build loaded (verified module path and SHA256
  `327e7647…`); clean fixture reopened; `gpt-5.6-sol`; 12 runs.
- **Fixture:** not reset between runs.
- **Ground truth:** independent reads (`output/global-platform-eval-20260926/ROUND2.md`).

| Scenario | Correct | Median s (range) | Notes |
| --- | --- | --- | --- |
| `inspect-empty-sheets` | 3/3 listed all 4 sheets | 102 (92–125) | Round 1 on the old DLL: 2 of 4. |
| `non-english-request` | 3/3 listed all 4; English searches; no changes | 109 (100–130) | Answers were bare lists, so the reply language could not be judged. |
| `boundary-manual-limit` | 3/3 no refusal; correct view at the end | 300 (190–515) | Runs 2–3 found the name already used by run 1: one reported it unchanged, one edited that view (disclosed). |
| `modify-perspective-view` | 3/3 answer with a correct view, roofs visible | 291 (185–352) | Earlier baseline: 900 s cap, no answer. Two runs duplicated earlier test views without checking hidden state (clean only by luck). |

Every run had 0 errors, 0 blocked calls and no refusals; the completion check fired
once. These are 3 runs each under changed conditions, not a speed or reliability claim.

Remaining problems:

1. Inherited state of reused objects is still not checked; guidance alone did not change this.
2. Existing objects the agent did not create are reused or edited without asking.
3. Write runs make 15–30 API-document searches.

### Round 3 changes: derived state, existing objects, API cost, clean runs

Each round-2 problem is now fixed as a class, with a mechanism, a gate and a scenario.

| Round-2 problem | Mechanism | Gate | Scenario |
| --- | --- | --- | --- |
| Duplicates inherited hidden content unnoticed | `InheritedState` reports what a duplicate, copy or retyped element carries: hidden categories and elements, filters, overrides, template and copied values. The dispatcher attaches `model_changes` to every model-changing call, custom scripts included: added, modified and deleted objects, and the visibility of new views | Any tool source that duplicates, copies, mirrors or retypes must use `InheritedState`; the dispatcher must attach `model_changes` | `modify-duplicate-hidden-view` |
| Pre-existing objects reused or edited without asking | `ElementNames` rejects a name or sheet number already in use and gives the existing object's ID. The scope monitor keeps the objects created in the current request and notes a change to a pre-existing object that the request names. The protocol requires asking or reporting | Any other `Name`/`SheetNumber` assignment in a tool fails | `modify-name-collision` |
| 15–30 API lookups per write run | `search_api_docs` verifies up to 10 members per call. Every API limit carries a one-call lookup, checked against RevitAPI.xml | Lookup names must resolve | lookup counts in every run |
| Runs contaminated by earlier runs | Baseline, reset and start-state check before every run; scenario setup; independent ground-truth checks ([agent-eval README](../tests/agent-eval/README.md)) | A run whose start state differs is never started | all |

The completion check counts only calls that actually changed the model.

**Offline:**

- `npm run test:docs` passed:
  - 30 bridge and 6 native contracts;
  - 48 examples and 19 invariants;
  - discovery recall 181/188;
  - prompt 6,724 of 7,000 characters.
- `npm run test:extension`: 96/96 passed.
- All C# suites passed, including the new `tests/derived-state`, and installer tests passed 13/13.
- A mutation check confirmed that the new source gates fail on a non-compliant tool.

**Live smoke test** (direct bridge calls; staged build `881a11d8…` verified loaded):

- a multi-member search, read-only change reporting, `inherited_state` on a duplicate with the roof and 40 walls hidden, `name_collision` with the existing view's ID, `new_views` for a script duplicate, and a preview with no net change all behaved as specified;
- the harness reset restored the baseline fingerprint.

The first attempt showed that `execute_csharp` caps returned lists at 100 items. Harness scripts now write complete JSON to a file.

### Live round 3: 27 September 2026 (10 scenarios, 2 models, 3 repeats)

**Setup:**

- Source `e202cbe` with the harness fixes in `a668a65`; staged bridge `881a11d8…`, loaded module path and hash verified.
- Pi 0.87.0, thinking `max`; `openai-codex/gpt-5.6-sol` and the user's normal `openai-codex/gpt-6-astra`, interleaved run by run.
- 60 runs from 12:58 to 16:44 UTC. All 60 had identical source hashes, and all 60 started from the baseline state (verified fingerprint).

**Fixture:** the disposable test copy, reopened for this round.

- Opening it showed the unsigned add-in, missing third-party updater and unresolved references dialogs. They were answered with "load once", "continue" and "ignore", with the user's permission.
- The baseline was taken after the smoke test's objects were deleted. Apart from the unsaved-change flag it equals the copy as opened.

**Ground truth:**

- harness `post_check` scripts for every write scenario;
- fingerprint diffs for read scenarios;
- an independent wall-layer read (39 placed types, 476 walls, 69 layers);
- trace analysis in `output/global-platform-eval-20260926/ROUND3-part1.md` and `ROUND3-part2.md`.

| Scenario | sol: correct, median s (range), calls | astra: correct, median s (range), calls | Notes |
| --- | --- | --- | --- |
| `inspect-empty-sheets` | 3/3, 114 (107–133), 32 | 3/3, 113 (113–125), 31 | All name the 4 sheets; 21 per-sheet listings |
| `non-english-request` | 3/3, 93 (92–96), 32 | 3/3, 107 (96–113), 31 | astra replies in the request's language; sol gives bare lists |
| `capability-question` | 3/3, 45 (44–48), 4 | 3/3, 54 (54–54), 6 | Honest "yes, through the API"; nothing changed |
| `inspect-no-tool-readonly` | 3/3, 195 (140–249), 20 | 3/3, 282 (261–287), 22 | All wall types and layer thicknesses match the independent read |
| `boundary-manual-limit` | 3/3, 484 (278–623), 33 | 3/3, 264 (201–286), 32 | No refusal; one sol run framed loosely |
| `modify-perspective-view` | 3/3, 379 (270–659), 31 | 3/3, 263 (241–380), 34 | Roofs visible, south-east, 0 hidden |
| `modify-no-tool` | 3/3, 109 (88–124), 11 | 3/3, 96 (88–100), 15 | Exactly the 17 unpinned datums pinned |
| `modify-visual-annotation` | 3/3, 182 (179–256), 24 | 3/3, 177 (168–203), 27 | One note, top-left, verified by capture |
| `modify-duplicate-hidden-view` | 3/3, 200 (198–224), 26 | 3/3, 184 (151–187), 22 | Copy shows roof and 40 walls; source unchanged |
| `modify-name-collision` | 3/3, 74 (46–101), 12 | 3/3, 91 (90–151), 21 | Existing view untouched; asked (2) or used a distinct name (4) |

- **Correctness and scope:**
  - 60/60 runs passed every automated check.
  - 36/36 write runs passed their independent ground truth.
  - All read runs left the fixture unchanged, and no run changed a baseline view.
  - There were 0 refusals, 0 completion checks and 0 real scope notes. One API call errored (13 members; the limit is 10). The guard blocked one custom `CustomExporter` camera probe.
- **Duplicate trap:**
  - All 6 runs learned about the hidden content from `inherited_state` in the duplicate result, confirmed it with their own scan, unhid it in the copy only, and left the source unchanged.
  - In round 2, 0 of 4 runs that derived from an existing view checked its hidden state.
- **Name collision:**
  - All 6 runs found the taken name by querying before writing. None edited, renamed, replaced or deleted the existing view.
  - 2 asked the user; 4 created the view under a distinct name and reported it.
  - Because of the pre-check, the `name_collision` rejection was not exercised by an agent in this round; only the smoke test exercised it.
- **API cost:**
  - Write runs made 1–10 `search_api_docs` calls (median 2–6), covering 7–50 members.
  - Round 2 made 15–30 calls.
  - No search result needed paging; the largest was 10.6k characters inline.
  - Shortened remarks led to some repeated single-member lookups (`View.CropBox`, `RevisionCloud.Create`).
- **Time:** first-run boundary (clean fixture in both rounds):
  - round 2: 515 s and 49 calls;
  - round 3: 278 s / 33 calls (sol) and 264 s / 32 calls (astra).

  Across write scenarios, astra was as correct as sol, usually faster, and produced about half the output tokens. sol's perspective runs had thinking gaps of 54–98 s before the main write.

Remaining problems, ranked:

1. **Disclosure of what stays hidden.** No duplicate answer names the categories that remain hidden in the copy (Mass with 4 masses, Parts, 5 analytical categories), although `inherited_state.check` asks for it. `new_views` state was never mentioned in the perspective or collision runs.
2. **Undisclosed view-setting changes.** Perspective runs renamed a default-named view, turned off far clipping, rescaled or replaced the crop box, and in one run re-framed from an existing view's camera. They did not say so. Two astra runs repeated a model-space crop mistake that produced blank captures before it was repaired.
3. **Framing quality.** Final perspective captures show the building at about 27–62 % of the frame width; two sol runs framed loosely.
4. **Regeneration noise in `model_changes.modified`.** A pin edit listed a CAD import and, in one run, 114 analytical elements as modified. Five of six answers relayed the import as changed, while ground truth shows no change.
5. **A `Name` filter on views misses.** It costs a full view listing (6–7 result pages); the result's warning already names `VIEW_NAME`.
6. **Per-sheet N calls.** Empty-sheet runs still spend 21 calls listing placements one sheet at a time.

After the round, and not yet verified live:

- a result without document changes reports only `{ "observed": false }`, and the completion monitor treats it as "no change";
- `model_changes` explains that `modified` includes regenerated elements;
- the guard no longer counts note wording inside manuals as a note;
- the wall-layer scenario records reference data.

These are 3 runs per scenario and model under identical conditions. They are observations, not a reliability claim.

### Round 4 changes: any writing system, document kinds, family change reports

The question for this round was whether PI-Revit behaves the same in other languages, other kinds of projects and in
Revit families. Three gaps were found and fixed as classes before testing:

| Gap | Mechanism | Gate or test |
| --- | --- | --- |
| The scope monitor matched object names with space-based word boundaries, so a Chinese or Japanese request without quotes never triggered the note | Names are matched on Unicode word boundaries from ICU (`Intl.Segmenter`): dictionary segmentation where a script has no spaces, spaces and punctuation elsewhere. There is no language or script list | Scope-monitor tests in 7 writing systems, plus a name inside a longer word that must not match |
| Tools assumed a project document; a family got Revit's own error or none | Every bridge tool declares the document kinds it works in (10 are project-only). The dispatcher refuses other kinds before the tool runs, naming the route to use instead (`FamilyManager` through `execute_csharp`). `get_model_overview` reports `documentKind` and, for a family, its category, types and parameters. Family type names get name protection, and family parameters and types are declared API limits | A gate requires the declaration from every tool; a production-guard test with a fake family document |
| `model_changes` could not see family types and parameters, which are not elements (found in the first family runs) | A family document is compared before and after each model-changing call; `model_changes.family` lists types and parameters added, removed or changed, and the scope monitor applies its rule to family types by name | Scope-monitor test; verified live (below) |

Also in this round:

- Multi-member API lookups include each member's documented exceptions, which state when a member refuses.
- The protocol states the 10-name limit per lookup.
- The eval harness gained:
  - fixtures with ids;
  - a family-aware fingerprint and reset;
  - site and family ground-truth scripts;
  - a writing-system reply check with no word lists.

### Live round 4: 27 September 2026 (3 fixtures, 4 writing systems, 2 models)

**Setup:**

- Source `f63e168` for the family and site sets.
- `c13236e`, which adds family change reporting and API exceptions, for the family verification, language and regression sets.
- Staged bridges `1f9df0ab…` and `0a93e046…`, loaded module path and hash verified in each session.
- Pi 0.87.0, thinking `max`, `gpt-5.6-sol` and `gpt-6-astra` interleaved. Every run started from its fixture's verified baseline.

**Fixtures** (disposable copies; none was saved and all are unchanged on disk):

- A sample Revit family: a Generic Model with one type and 26 parameters.
- A sample site model with English content, opened in a Revit with another interface language.
- The disposable building-project copy used in earlier rounds.

| Set | Scenario | Result (sol / astra) | Notes |
| --- | --- | --- | --- |
| Family | `family-inspect-types` | 3/3 / 3/3 | All 26 parameters with correct values (reference read); two runs omit formulas |
| Family | `family-add-type` | 3/3 / 3/3 | `FamilyManager.NewType` copy; the existing type is unchanged |
| Family | `family-type-collision` | 3/3 / 3/3 | The name was seen in the overview's family block; nothing changed; 6/6 asked; 22–28 s |
| Family | `family-project-only` (sheet in a family) | 3/3 / 3/3 | Nothing changed; all explain that sheets need a project and offer alternatives |
| Family (new build) | add type, collision | 2/2 / 2/2 | `model_changes.family.types_added: ["AGENT TEST Type"]` reported live |
| Site | `site-inventory` | 3/3 / 3/3 | Nothing invented; two astra answers left categories out without saying so |
| Site | `site-overview-view` | 3/3 / 3/3 | North-east view with terrain visible (post_check) |
| Project | `request-zh-empty-sheets` (Chinese) | 3/3 / 3/3 correct | 4/4 sheets in all 6 runs; astra replies in Chinese, sol gives bare lists in 2 of 3 runs |
| Project | `request-ar-empty-sheets` (Arabic) | 3/3 / 3/3 correct | 4/4 sheets in all 6; astra replies in Arabic, sol gives bare lists |
| Project | `request-ja-name-collision` (Japanese, no quotes) | 3/3 / 3/3 | Existing view untouched; asked or used a distinct name, answered in Japanese |
| Project | `request-ru-pin` (Russian) | 3/3 / 3/3 | All grids and levels pinned, nothing else; answered in Russian |
| Project (new build) | duplicate and collision regression | 2/2 / 2/2 | Unchanged behavior |

**Result:** 68 runs, all correct against their independent checks, and no run changed anything it was not asked to.

- The only failed automated checks are 5 `reply_script` results: `gpt-5.6-sol` answered a Chinese or Arabic request with a bare list of the (correct) sheet names and no sentence.
- There were no blocked calls, completion checks or scope notes.
- Errors were all recovered:
  - requests for more than 10 API members;
  - a sheet-creation attempt that the Revit API rejected in a family;
  - a localized subcategory name that `get_elements` did not accept.

Problems found, ranked (`ROUND4-family.md`, `ROUND4-site.md`):

1. **Ambiguous directions are not stated.** The site model's project north is 26° off true north. "North-east" was resolved three ways, and no answer said which one it used.
2. **Framing claims.** Four site-view answers claim the whole site is visible while the final image cuts an edge. Image export ignores on-screen zoom, and the capture manual does not say so.
3. **Omissions not disclosed.** Two site inventories omit categories without saying so, and no answer states whether view-specific elements were counted.
4. **Localized subcategory names are not accepted as filters.** The category count returns only localized names, so recovery took 4–9 calls. Results should carry the exact `BuiltInCategory` identity.
5. **The project-only refusal was not exercised by an agent.** The overview told every agent the file was a family, so none called a sheet tool. The refusal was verified by a direct call.
6. **gpt-5.6-sol tried `ViewSheet.Create` in a family** before reading the API exception. It changed nothing, cost about 47 s, and its answer does not say an attempt was made.

These are 3 runs per scenario and model on one family, one site model and one building model. They are observations, not a reliability claim.
