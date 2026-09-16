const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { test } = require('node:test');

const root = path.resolve(__dirname, '../..');
const quote = value => "'" + value.replaceAll("'", "''") + "'";

// Run the actual PowerShell entry points in a disposable Revit layout. No real
// SDK build, browser, network, or user add-in directory is used.
function runFlow({ script = 'deploy.ps1', sdk = '9.0.312', versions = [2027], args = [] } = {}) {
    const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'pi-revit-sdk-'));
    try {
        const programFiles = path.join(fixture, 'Program Files');
        const appData = path.join(fixture, 'AppData');
        for (const version of versions) {
            const apiDir = path.join(programFiles, 'Autodesk', `Revit ${version}`);
            fs.mkdirSync(apiDir, { recursive: true });
            fs.writeFileSync(path.join(apiDir, 'RevitAPI.dll'), 'fixture');
        }
        fs.mkdirSync(appData);
        const wrapper = path.join(fixture, 'run.ps1');
        fs.writeFileSync(wrapper, `
$ErrorActionPreference = 'Stop'
$env:ProgramFiles = ${quote(programFiles)}
$env:APPDATA = ${quote(appData)}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq '--version') { ${quote(sdk)}; return }
    if ($args[0] -eq 'build') { Write-Host ('BUILD_CALLED ' + ($args -join ' ')); return }
    throw 'Unexpected dotnet invocation'
}
function Start-Process { throw 'Tests must not open a browser' }
function Read-Host { throw 'Redirected input must not prompt' }
& ${quote(path.join(root, 'scripts', script))} ${args.map(arg => /^-[A-Za-z]+$/.test(arg) ? arg : quote(arg)).join(' ')}
exit $LASTEXITCODE
`, 'utf8');
        const result = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', wrapper], {
            encoding: 'utf8', timeout: 15000, cwd: fixture,
        });
        assert.ifError(result.error);
        assert.deepEqual(fs.readdirSync(appData), [], 'prerequisite checks must not deploy files');
        return { status: result.status, output: result.stdout + result.stderr };
    } finally {
        const resolved = path.resolve(fixture);
        assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()));
        assert.ok(path.basename(resolved).startsWith('pi-revit-sdk-'));
        fs.rmSync(resolved, { recursive: true, force: true });
    }
}

test('deployment with SDK 9 and Revit 2027 exits cleanly before compilation', { skip: process.platform !== 'win32' }, () => {
    const result = runFlow();
    assert.equal(result.status, 1);
    assert.match(result.output, /Detected Revit: 2027/);
    assert.match(result.output, /currently use .NET SDK 9\.0\.312/);
    assert.match(result.output, /download\/dotnet\/10\.0/);
    assert.doesNotMatch(result.output, /BUILD_CALLED|CategoryInfo|NETSDK1045/);
});

test('mixed installations are checked before the first build', { skip: process.platform !== 'win32' }, () => {
    const result = runFlow({ versions: [2025, 2027] });
    assert.equal(result.status, 1);
    assert.match(result.output, /Detected Revit: 2025, 2027/);
    assert.doesNotMatch(result.output, /BUILD_CALLED/);
});

test('successful preflight does not build or deploy', { skip: process.platform !== 'win32' }, () => {
    for (const [sdk, versions] of [['10.0.100', [2027]], ['9.0.312', [2025, 2026]]]) {
        const result = runFlow({ sdk, versions, args: ['-CheckOnly'] });
        assert.equal(result.status, 0, result.output);
        assert.doesNotMatch(result.output, /BUILD_CALLED/);
    }
});

test('download offer never waits for redirected stdin', { skip: process.platform !== 'win32' }, () => {
    const result = runFlow({ args: ['-CheckOnly', '-OfferDownload'] });
    assert.equal(result.status, 1);
    assert.match(result.output, /download\/dotnet\/10\.0/);
    assert.doesNotMatch(result.output, /Open the SDK download page now|Redirected input must not prompt/);
});

test('direct default and .NET 10 builds stop before invoking build with SDK 9', { skip: process.platform !== 'win32' }, () => {
    for (const args of [[], ['-TargetFramework', 'net10.0-windows']]) {
        const result = runFlow({ script: 'build.ps1', args });
        assert.equal(result.status, 1, result.output);
        assert.match(result.output, /requires the .NET 10 SDK/);
        assert.doesNotMatch(result.output, /BUILD_CALLED|CategoryInfo/);
    }
});

test('a direct .NET 8 build still works with SDK 9 and limits restore to its target', { skip: process.platform !== 'win32' }, () => {
    const result = runFlow({ script: 'build.ps1', args: ['-TargetFramework', 'net8.0-windows'] });
    assert.equal(result.status, 0, result.output);
    assert.match(result.output, /BUILD_CALLED/);
    assert.match(result.output, /-p:TargetFrameworks=net8\.0-windows/);
});
