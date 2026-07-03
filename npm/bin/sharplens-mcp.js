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

// The installed version of the SharpLensMcp global tool, or null when it is not
// installed. `dotnet tool list --global` lowercases the package id and prints it
// as `sharplensmcp   <version>   sharplens`. This is a local lookup — no network.
function installedVersion() {
  try {
    const output = execSync("dotnet tool list --global", {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"],
    });
    for (const line of output.split("\n")) {
      const match = line.match(/^sharplensmcp\s+(\S+)/i);
      if (match) {
        return match[1];
      }
    }
  } catch {
    // dotnet missing or the command failed — treat the tool as not installed.
  }
  return null;
}

function launch() {
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
}

if (!hasDotnet()) {
  process.stderr.write(
    "Error: .NET SDK not found. SharpLensMcp requires .NET 8.0 SDK or later.\n"
  );
  process.exit(1);
}

const current = installedVersion();

if (current === version) {
  // Already on the matching version — skip the install/update round-trip
  // entirely so startup stays fast and works offline.
  process.stderr.write(`SharpLensMcp v${version} already installed.\n`);
  launch();
} else {
  process.stderr.write(`Ensuring SharpLensMcp v${version} is installed...\n`);
  try {
    execSync(
      `dotnet tool update --global SharpLensMcp --version ${version}`,
      { stdio: ["ignore", "ignore", "inherit"] }
    );
    launch();
  } catch {
    // The update failed (commonly: offline). If a copy is already installed, run
    // it instead of failing hard; otherwise there is nothing to launch.
    if (current !== null) {
      process.stderr.write(
        `Warning: could not update to SharpLensMcp v${version} (offline?). ` +
          `Launching the installed v${current}.\n`
      );
      launch();
    } else {
      process.stderr.write(
        `Error: Failed to install SharpLensMcp v${version}.\n`
      );
      process.exit(1);
    }
  }
}
