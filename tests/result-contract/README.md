# Offline result contract checks

These checks exercise the real extension with intercepted `fetch` calls and the
real C# bridge response builder with stub collaborators. They do not contact the
Revit bridge, load Revit API assemblies, or modify a model.

For the TypeScript extension checks, point `PI_CODING_AGENT_PATH` at an installed
`@earendil-works/pi-coding-agent` package so the harness uses Pi's own `jiti` and
`typebox` dependencies. If those dependencies are resolvable from this repository,
the variable is unnecessary.

```powershell
$env:PI_CODING_AGENT_PATH = 'C:\path\to\node_modules\@earendil-works\pi-coding-agent'
node tests/result-contract/result-contract.test.mjs
dotnet run --project tests/result-contract/bridge-response-tests.csproj
```

Running the Node test module directly avoids spawning a child test worker in
restricted environments. It still uses Node's test runner and emits TAP output.

The bridge project accepts `-p:BridgeSource=<absolute BridgeServer.cs path>` for
testing an untouched baseline source file against the same response assertions.

Coverage includes requested values, every row of a returned page, the inline
limit boundary, saved-file completeness, reconstruction using bounded result
fragments, JSON escaping, UTF-16 offsets, one-character/end pages, invalid IDs and
ranges, missing saved files, null/legacy results, and bridge error propagation.
It also verifies that one failed temporary-directory creation does not prevent
later oversized results from being saved after the filesystem issue is resolved.

The test harness deletes only its own mock discovery directory and saved-result
files. The result directory itself is unique under the OS temporary directory.
