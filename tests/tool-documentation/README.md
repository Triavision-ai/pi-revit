# Offline documentation contract validation

From a source checkout with an installed .NET SDK 8+ and Pi's existing `jiti`
and `typebox` dependencies:

```powershell
$env:PI_CODING_AGENT_PATH = 'C:\path\to\node_modules\@earendil-works\pi-coding-agent'
npm.cmd run test:docs
```

The Node runner locates Pi dependencies using the explicit variable or the
installation next to the Node executable. It installs nothing. The .NET project
uses SDK-shipped Roslyn and targets that SDK's runtime major; NuGet sources are
cleared and there are no package dependencies.

The extractor keeps declarative metadata/schema members from actual bridge tool
source, selected shared schema helpers, the real identity-required predicate and
the real `ToolRegistry`. It replaces execution bodies with throwing stubs and
uses minimal Autodesk context type stand-ins. A constructor or unsupported source
refactor fails validation and requires review rather than a copied schema fallback.

The Node check registers the actual extension using those descriptors, with
isolated `APPDATA`, intercepted metadata responses, and no background polling.
Every network route other than fixture `/ping` and `/tools` is rejected, and no
registered tool is executed. It checks:

- Manifest inventory against actual bridge and native registrations.
- Exactly one manual per public name, no orphan tool manuals.
- Every manual's JSON input example against the final registered schema,
  including identity and retry overlays; examples may contain discovered-ID placeholders.
- Malformed required-field/retry-ID variations are rejected by the validator.
- Local Markdown links in the operational skill tree, architecture/evaluation
  guides, contributor instructions and readme files resolve.

JSON fences in a tool manual are input examples. Use a different fence label for
illustrative output fragments. Examples must use advertised argument keys; free
JSON maps remain allowed where the schema supports them.

The checker writes build artifacts to this test project's ignored `bin`/`obj`
directories and creates a private temporary fixture which it removes afterward.
It never loads Autodesk assemblies or connects to a live bridge. It validates
schema/structure, not conditional action rules, the truth of every prose statement,
execution on a real model, skill triggering or agent performance. Use the relevant
behavior tests and [evaluation plan](../../docs/evaluation.md) for those concerns.
