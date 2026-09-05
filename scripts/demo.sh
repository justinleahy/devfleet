#!/usr/bin/env bash
# Loopback demonstration hosts. Default and --smoke never call providers and never
# treat project registration as a completed SPEC demonstration. The canonical
# request is submitted through the web UI. RUN_REAL_* starts the node so that a
# UI-submitted request can launch the official CLIs (quota).
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
FIXTURE_SRC="${REPO_ROOT}/demo/health-details-fixture"

SMOKE=0
if [[ "${1:-}" == "--smoke" ]]; then
    SMOKE=1
    shift
fi

REAL_PIPELINE=0
if [[ -n "${RUN_REAL_PI_TESTS:-}" || -n "${RUN_REAL_CLAUDE_TESTS:-}" || -n "${RUN_REAL_ANTIGRAVITY_TESTS:-}" ]]; then
    REAL_PIPELINE=1
fi

for bin in git dotnet curl python3; do
    if ! command -v "${bin}" >/dev/null 2>&1; then
        echo "Missing prerequisite: ${bin}" >&2
        exit 1
    fi
done

if [[ ! -d "${FIXTURE_SRC}" ]]; then
    echo "Fixture missing: ${FIXTURE_SRC}" >&2
    exit 1
fi

if [[ "${SMOKE}" -eq 1 && "${REAL_PIPELINE}" -eq 1 ]]; then
    echo "--smoke ignores RUN_REAL_* so quota is never spent." >&2
    REAL_PIPELINE=0
fi

if [[ "${SMOKE}" -eq 1 ]]; then
    PI_CC_DATA="$(mktemp -d "${TMPDIR:-/tmp}/pi-cc-demo-smoke.XXXXXX")"
    export PI_CC_DATA
fi

export PI_CC_DATA="${PI_CC_DATA:-${XDG_DATA_HOME:-$HOME/.local/share}/pi-command-center}"
"${SCRIPT_DIR}/setup-local.sh"

# shellcheck disable=SC1091
source "${PI_CC_DATA}/local.env"

PORT="${PI_CC_PORT:-5057}"
URL="http://127.0.0.1:${PORT}"
APPROVED="${PI_CC_APPROVED_ROOT:-${PI_CC_DATA}/approved}"
mkdir -p "${APPROVED}"
chmod 0700 "${APPROVED}" "${PI_CC_DATA}"

WORKSPACE="${APPROVED}/health-details-fixture-$$"
mkdir -p "${WORKSPACE}"
cp -a "${FIXTURE_SRC}/." "${WORKSPACE}/"
git -C "${WORKSPACE}" init -q -b main
git -C "${WORKSPACE}" config user.email "demo@example.invalid"
git -C "${WORKSPACE}" config user.name "Command Center Demo"
git -C "${WORKSPACE}" add -A
git -C "${WORKSPACE}" commit -q -m "Initial health-details fixture"

CP_PID=""
NODE_PID=""
cleanup() {
    if [[ -n "${NODE_PID}" ]] && kill -0 "${NODE_PID}" 2>/dev/null; then
        kill "${NODE_PID}" 2>/dev/null || true
        wait "${NODE_PID}" 2>/dev/null || true
    fi
    if [[ -n "${CP_PID}" ]] && kill -0 "${CP_PID}" 2>/dev/null; then
        kill "${CP_PID}" 2>/dev/null || true
        wait "${CP_PID}" 2>/dev/null || true
    fi
}
trap cleanup EXIT

export ASPNETCORE_URLS="${URL}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ConnectionStrings__ControlPlane="Data Source=${PI_CC_DATA}/controlplane.db"
export Projects__ApprovedRoots__0="${APPROVED}"
export ControlPlane__BaseUrl="${URL}"
export Node__ControlPlaneUrl="${URL}"
export Node__EventSpoolPath="${PI_CC_DATA}/node-spool.db"
export Node__RequireCleanStart="false"
export Admin__Username
export Admin__PasswordFile
export NodeAuthentication__CredentialFile
export NodeAuthentication__Header
export NodeAuthentication__Scheme

dotnet run --project "${REPO_ROOT}/src/PiCommandCenter.ControlPlane" --no-launch-profile \
    > "${PI_CC_DATA}/controlplane.log" 2>&1 &
CP_PID=$!

ready=0
for _ in $(seq 1 90); do
    if curl -sf "${URL}/health" >/dev/null 2>&1; then
        ready=1
        break
    fi
    if ! kill -0 "${CP_PID}" 2>/dev/null; then
        echo "Control Plane exited. Log:" >&2
        cat "${PI_CC_DATA}/controlplane.log" >&2 || true
        exit 1
    fi
    sleep 0.5
done
if [[ "${ready}" -ne 1 ]]; then
    echo "Control Plane did not become healthy on ${URL}/health" >&2
    cat "${PI_CC_DATA}/controlplane.log" >&2 || true
    exit 1
fi

if [[ "${REAL_PIPELINE}" -eq 1 ]]; then
    dotnet run --project "${REPO_ROOT}/src/PiCommandCenter.Node" --no-launch-profile \
        > "${PI_CC_DATA}/node.log" 2>&1 &
    NODE_PID=$!
fi

COOKIE_JAR="${PI_CC_DATA}/demo.cookies"
touch "${COOKIE_JAR}"
chmod 0600 "${COOKIE_JAR}"
PASSWORD="$(cat "${PI_CC_ADMIN_PASSWORD_ONCE_FILE}")"

curl -sf -c "${COOKIE_JAR}" -b "${COOKIE_JAR}" "${URL}/login" -o "${PI_CC_DATA}/login.html"
TOKEN="$(python3 - "${PI_CC_DATA}/login.html" <<'PY'
import re, sys
html = open(sys.argv[1], encoding="utf-8").read()
match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
if not match:
    match = re.search(r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', html)
if not match:
    sys.exit("login page missing antiforgery token")
print(match.group(1))
PY
)"

curl -sf -c "${COOKIE_JAR}" -b "${COOKIE_JAR}" -X POST "${URL}/account/login" \
    --data-urlencode "username=${Admin__Username}" \
    --data-urlencode "password=${PASSWORD}" \
    --data-urlencode "returnUrl=/" \
    --data-urlencode "__RequestVerificationToken=${TOKEN}" \
    -H "RequestVerificationToken: ${TOKEN}" \
    -o /dev/null

register_body="$(curl -sf -c "${COOKIE_JAR}" -b "${COOKIE_JAR}" -X POST "${URL}/api/projects" \
    -H "Content-Type: application/json" \
    -H "RequestVerificationToken: ${TOKEN}" \
    -d "{\"displayName\":\"Health details fixture\",\"repositoryPath\":\"${WORKSPACE}\",\"defaultBranch\":\"main\",\"enabled\":true,\"maxActiveWriteRequests\":2,\"maxReadOnlyRequests\":4,\"maxChildAgentsPerRequest\":3,\"requireCleanStart\":false,\"createRequestBranch\":false,\"createRequestCommit\":false,\"autoMerge\":false}")"

project_id="$(printf '%s' "${register_body}" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p' | head -n1)"
if [[ -z "${project_id}" ]]; then
    echo "Failed to register fixture project: ${register_body}" >&2
    exit 1
fi

if [[ "${REAL_PIPELINE}" -eq 0 ]]; then
    echo "Node is intentionally stopped; queued UI requests cannot spend provider quota."
fi

echo "Hosts are up. This is not a completed demonstration."
echo "  Control Plane: ${URL}"
echo "  Login:         ${URL}/login  (admin; password in ${PI_CC_ADMIN_PASSWORD_ONCE_FILE})"
echo "  Health:        ${URL}/health"
echo "  Project URL:   ${URL}/projects/${project_id}"
echo "  Project ID:    ${project_id}"
echo "  Fixture:       ${WORKSPACE}"
echo "Submit the canonical request from the project page (New request → Queue request)."
echo "Guide: ${REPO_ROOT}/demo/FIRST-DEMO.md"


if [[ "${SMOKE}" -eq 1 ]]; then
    echo "Smoke mode: quota-free shutdown. Registration is not SPEC success."
    exit 0
fi

echo "Press Ctrl+C to stop the demonstration hosts."
wait "${CP_PID}"
