#!/usr/bin/env bash
# Milestone 0 verification gate.
# Resolves all paths from this script's location, so it works from any caller cwd.
# Strict mode: any failing command stops the script immediately.
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

cd "${REPO_ROOT}"

echo "==> dotnet restore"
dotnet restore

echo "==> dotnet build --no-restore"
dotnet build --no-restore

echo "==> dotnet test --no-build"
dotnet test --no-build

cd "${REPO_ROOT}/runtime"

if [[ -f package-lock.json ]]; then
    echo "==> npm ci"
    npm ci
else
    echo "==> npm install (no package-lock.json found)"
    npm install
fi

echo "==> npm run typecheck"
npm run typecheck

echo "==> npm test"
npm test

echo "==> All verification steps passed."
