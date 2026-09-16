const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const installerPath = path.join(root, "bin/pi-revit.js");
const installerSource = readFileSync(installerPath, "utf8");
const currentVersion = JSON.parse(readFileSync(path.join(root, "package.json"), "utf8")).version;

class InstallerExit extends Error {
	constructor(code) { super(`Installer exited with ${code}`); this.code = code; }
}

// Execute the complete production entry point. No real child process, stdin,
// registry, Revit bridge, deployment, network or filesystem mutation is allowed.
function runInstaller({ argv = [], version = currentVersion, revitRunning = [false], installStatus = 0, preflightStatus = 0 } = {}) {
	const calls = [];
	const output = [];
	const errors = [];
	let prompts = 0;
	let runningChecks = 0;
	let exitCode = 0;
	const packagePath = path.join(root, "package.json");
	const scripts = ["deploy.ps1", "setup-workspace.ps1"].map(name => path.join(root, "scripts", name));
	const childProcess = {
		spawnSync(command, args, options) {
			const copiedArgs = Array.from(args);
			calls.push({ command, args: copiedArgs, options });
			if (command === "where.exe") {
				assert.ok(copiedArgs.length === 1 && ["pi", "dotnet"].includes(copiedArgs[0]));
				return { status: 0 };
			}
			if (command === "powershell.exe" && copiedArgs.includes("-Command")) {
				assert.match(copiedArgs.at(-1), /Get-Process Revit/);
				assert.ok(runningChecks < revitRunning.length, "unexpected extra Revit-running check");
				return { status: revitRunning[runningChecks++] ? 0 : 1 };
			}
			if (command === "cmd.exe") return { status: installStatus };
			if (command === "powershell.exe" && copiedArgs.includes("-File")) {
				assert.ok(scripts.includes(copiedArgs[copiedArgs.indexOf("-File") + 1]), "only package deployment/setup scripts may be requested");
				if (copiedArgs.includes("-CheckOnly")) return { status: preflightStatus };
				return { status: 0 };
			}
			throw new Error(`Unexpected subprocess request: ${command}`);
		},
	};
	const fakeFs = {
		existsSync(file) { assert.ok(scripts.includes(file)); return true; },
		readSync(fd, buffer, offset, count) {
			assert.equal(fd, 0);
			assert.equal(offset, 0);
			assert.equal(count, 1);
			prompts++;
			return 1;
		},
	};
	const context = {
		__dirname: path.dirname(installerPath),
		Buffer,
		console: { log: (...parts) => output.push(parts.join(" ")), error: (...parts) => errors.push(parts.join(" ")) },
		process: { argv: ["node", installerPath, ...argv], platform: "win32", exit: code => { throw new InstallerExit(code); } },
		require(id) {
			if (id === "node:child_process") return childProcess;
			if (id === "node:fs") return fakeFs;
			if (id === "node:path") return path;
			if (id === packagePath) return { version };
			throw new Error(`Unexpected module request: ${id}`);
		},
	};
	try { vm.runInNewContext(installerSource, context, { filename: installerPath, timeout: 1000 }); }
	catch (error) {
		if (!(error instanceof InstallerExit)) throw error;
		exitCode = error.code;
	}
	return { calls, output: output.join("\n"), errors: errors.join("\n"), prompts, runningChecks, exitCode };
}

for (const version of [currentVersion, "8.9.10-rc.2"]) {
	test(`installer pairs the extension and deployed source for package version ${version}`, () => {
		const result = runInstaller({ version });
		assert.equal(result.exitCode, 0);
		const installation = result.calls.filter(call => call.command === "cmd.exe");
		assert.equal(installation.length, 1);
		assert.deepEqual(installation[0].args, ["/d", "/s", "/c", `pi install npm:pi-revit@${version}`]);
		const preflight = result.calls.find(call => call.args.includes("-CheckOnly"));
		assert.ok(preflight.args.includes("-OfferDownload"));
		assert.ok(result.calls.indexOf(preflight) < result.calls.indexOf(installation[0]), "SDK check happens before package installation");
		const deployment = result.calls.filter(call => call.args.includes("-File") && !call.args.includes("-CheckOnly"));
		assert.deepEqual(deployment.map(call => call.args.at(-1)), ["deploy.ps1", "setup-workspace.ps1"].map(name => path.join(root, "scripts", name)));
		assert.ok(result.calls.indexOf(installation[0]) < result.calls.indexOf(deployment[0]), "matching extension installs before bridge deployment");
		assert.equal(result.prompts, 0);
	});
}

test("both help flags show the exact package version without checking or installing anything", () => {
	for (const flag of ["--help", "-h"]) {
		const result = runInstaller({ argv: [flag] });
		assert.equal(result.exitCode, 0);
		assert.ok(result.output.includes(`pi install npm:pi-revit@${currentVersion}`));
		assert.equal(result.calls.length, 0);
		assert.equal(result.prompts, 0);
	}
});

test("Revit still running after the prompt stops before installation and deployment", () => {
	const result = runInstaller({ revitRunning: [true, true] });
	assert.equal(result.exitCode, 1);
	assert.equal(result.prompts, 1);
	assert.equal(result.runningChecks, 2);
	assert.match(result.errors, /Revit is still running/);
	assert.equal(result.calls.filter(call => call.command === "cmd.exe" || (call.args.includes("-File") && !call.args.includes("-CheckOnly"))).length, 0);
	assert.ok(!result.output.includes("pi-revit installed."));
});

test("closing Revit at the prompt permits installation only after the second check", () => {
	const result = runInstaller({ revitRunning: [true, false] });
	assert.equal(result.exitCode, 0);
	assert.equal(result.prompts, 1);
	assert.equal(result.runningChecks, 2);
	const checks = result.calls.filter(call => call.args.includes("-Command"));
	const installation = result.calls.find(call => call.command === "cmd.exe");
	assert.ok(result.calls.indexOf(checks[1]) < result.calls.indexOf(installation));
});

test("a failed matching-package installation never deploys or reports success", () => {
	const result = runInstaller({ installStatus: 7 });
	assert.equal(result.exitCode, 7);
	assert.equal(result.calls.filter(call => call.args.includes("-File") && !call.args.includes("-CheckOnly")).length, 0);
	assert.ok(!result.output.includes("pi-revit installed."));
});

test("a failed SDK prerequisite check stops before prompting to close Revit or installing anything", () => {
	const result = runInstaller({ preflightStatus: 1 });
	assert.equal(result.exitCode, 1);
	assert.equal(result.prompts, 0);
	assert.equal(result.runningChecks, 0);
	assert.equal(result.calls.filter(call => call.command === "cmd.exe").length, 0);
	assert.equal(result.calls.filter(call => call.args.includes("-File")).length, 1);
	assert.ok(!result.output.includes("pi-revit installed."));
});
