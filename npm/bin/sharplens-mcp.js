#!/usr/bin/env node

const { execSync, spawn } = require("child_process");
const { version } = require("../package.json");

function hasDotnet() {
  try {
    execSync("dotnet --version", { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

if (!hasDotnet()) {
  process.stderr.write(
    "Error: .NET SDK not found. SharpLensMcp requires .NET 8.0 SDK or later.\n"
  );
  process.exit(1);
}

process.stderr.write(`Ensuring SharpLensMcp v${version} is installed...\n`);
try {
  execSync(
    `dotnet tool update --global SharpLensMcp --version ${version}`,
    { stdio: ["ignore", "ignore", "inherit"] }
  );
} catch {
  process.stderr.write(
    `Error: Failed to install/update SharpLensMcp v${version}.\n`
  );
  process.exit(1);
}

const args = process.argv.slice(2);
const child = spawn("sharplens", args, {
  stdio: "inherit",
  shell: true,
});

child.on("exit", (code) => process.exit(code ?? 0));
child.on("error", (err) => {
  process.stderr.write(`Error: Failed to start sharplens: ${err.message}\n`);
  process.exit(1);
});
