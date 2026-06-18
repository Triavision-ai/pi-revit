#!/usr/bin/env node
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const scriptsDir = path.join(root, "scripts");

function usage() {
	console.log(`pi-revit installer\n\nUsage:\n  npx.cmd -y pi-revit\n\nWhat it does on Windows:\n  1. Runs: pi install npm:pi-revit\n  2. Builds and deploys the Revit bridge add-in\n  3. Creates the Documents\\pi-revit workspace and global pi-revit command\n\nClose Revit before running. Revit 2025, 2026, or 2027 and the matching .NET SDK are required.`);
}

function fail(message) {
	console.error(`\npi-revit install failed: ${message}`);
	process.exit(1);
}

function commandExists(command) {
	return spawnSync("where.exe", [command], { stdio: "ignore" }).status === 0;
}

function run(title, command, args) {
	console.log(`\n==> ${title}`);
	const result = spawnSync(command, args, { stdio: "inherit", shell: false });
	if (result.error) fail(`${command} could not be started: ${result.error.message}`);
	if (result.status !== 0) process.exit(result.status ?? 1);
}

function runCmd(title, commandLine) {
	console.log(`\n==> ${title}`);
	// On Windows, npm/pi are usually .cmd shims. Run them through cmd.exe so PATHEXT
	// resolution works from npx's Node process just like it does in PowerShell/CMD.
	const result = spawnSync("cmd.exe", ["/d", "/s", "/c", commandLine], { stdio: "inherit" });
	if (result.error) fail(`${commandLine} could not be started: ${result.error.message}`);
	if (result.status !== 0) process.exit(result.status ?? 1);
}

function runPowerShellScript(scriptName) {
	const scriptPath = path.join(scriptsDir, scriptName);
	if (!fs.existsSync(scriptPath)) fail(`missing script: ${scriptPath}`);
	run(scriptName, "powershell.exe", ["-ExecutionPolicy", "Bypass", "-File", scriptPath]);
}

function revitIsRunning() {
	const result = spawnSync(
		"powershell.exe",
		["-NoProfile", "-Command", "if (Get-Process Revit -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"],
		{ stdio: "ignore" },
	);
	return result.status === 0;
}

function waitForEnter() {
	console.log("\nRevit appears to be running. Close Revit now, then press Enter to continue (or Ctrl+C to cancel).");
	try {
		fs.readSync(0, Buffer.alloc(1), 0, 1);
	} catch {
		// Non-interactive shell: fall through to the second running check.
	}
}

if (process.argv.includes("--help") || process.argv.includes("-h")) {
	usage();
	process.exit(0);
}

if (process.platform !== "win32") {
	fail("pi-revit's Revit bridge installer supports Windows only.");
}

if (!commandExists("pi")) {
	fail("the 'pi' command was not found on PATH. Install Pi first: npm install -g --ignore-scripts @earendil-works/pi-coding-agent");
}

if (!commandExists("dotnet")) {
	fail("the 'dotnet' command was not found on PATH. Install the .NET SDK required by your Revit version.");
}

if (revitIsRunning()) {
	waitForEnter();
	if (revitIsRunning()) fail("Revit is still running. Close Revit and run 'npx.cmd -y pi-revit' again.");
}

console.log("pi-revit full installer");
console.log("This installs the Pi package, deploys the Revit add-in, and creates the workspace/global command.");

runCmd("Install the Pi package from npm", "pi install npm:pi-revit");
runPowerShellScript("deploy.ps1");
runPowerShellScript("setup-workspace.ps1");

console.log("\npi-revit installed.");
console.log("Next steps:");
console.log("  1. Start Revit and click Always Load if prompted.");
console.log("  2. Open a project.");
console.log("  3. Run: pi-revit");
