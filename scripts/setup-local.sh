#!/usr/bin/env bash
# Create the private native data tree, preserve authentication material, and
# write the environment consumed by the systemd user services.
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

DATA_DIR="${PI_CC_DATA:-${HOME}/.local/share/devfleet}"
INSTALL_ROOT="${PI_CC_INSTALL_ROOT:-${HOME}/.local/lib/devfleet}"
ADMIN_USER="${PI_CC_ADMIN_USERNAME:-admin}"
HASH_FILE="${PI_CC_ADMIN_PASSWORD_FILE:-${DATA_DIR}/admin.password.hash}"
PLAIN_FILE="${PI_CC_ADMIN_PASSWORD_ONCE_FILE:-${DATA_DIR}/admin.password}"
NODE_ID_FILE="${DATA_DIR}/node-id"
NODE_FILE="${DATA_DIR}/node.token"
NODE_CREDENTIAL_DIR="${DATA_DIR}/node-credentials"
DATA_PROTECTION_DIR="${DATA_DIR}/data-protection-keys"
PI_AGENT_DIR="${DATA_DIR}/pi-agent"
APPROVED_ROOT="${PI_CC_APPROVED_ROOT:-${HOME}/Developer}"
CLAUDE_CREDENTIAL_PATH="${DEVFLEET_CLAUDE_CREDENTIAL_PATH:-${HOME}/.claude/.credentials.json}"
SERVICE_PATH="${HOME}/.local/bin:${HOME}/bin:/usr/local/bin:/usr/bin:/bin"
PORT="${PI_CC_PORT:-5057}"
BIND_ADDRESS="${DEVFLEET_BIND_ADDRESS:-127.0.0.1}"
NODE_DISPLAY_NAME="${DEVFLEET_NODE_DISPLAY_NAME:-$(hostname)}"

require_absolute_path() {
    local name="$1"
    local path="$2"

    if [[ "${path}" != /* ]]; then
        echo "${name} must be an absolute host path: ${path}" >&2
        exit 1
    fi
}

resolve_executable() {
    local name="$1"
    local configured="$2"
    local resolved

    if [[ "${configured}" == */* ]]; then
        resolved="${configured}"
    else
        resolved="$(PATH="${SERVICE_PATH}:${PATH}" command -v -- "${configured}" || true)"
    fi

    if [[ -z "${resolved}" || "${resolved}" != /* || ! -x "${resolved}" ]]; then
        echo "${name} executable was not found as an executable absolute host path: ${configured}" >&2
        exit 1
    fi

    printf '%s' "${resolved}"
}
resolve_optional_executable() {
    local name="$1"
    local configured="$2"
    local resolved

    if [[ "${configured}" == */* ]]; then
        if [[ "${configured}" != /* || ! -x "${configured}" ]]; then
            echo "${name} executable is not an executable absolute host path: ${configured}" >&2
            exit 1
        fi
        printf '%s' "${configured}"
        return
    fi

    resolved="$(PATH="${SERVICE_PATH}:${PATH}" command -v -- "${configured}" || true)"
    printf '%s' "${resolved:-${configured}}"
}


write_environment_value() {
    local name="$1"
    local value="$2"

    if [[ "${value}" == *"'"* || "${value}" == *$'\n'* || "${value}" == *$'\r'* ]]; then
        echo "${name} contains characters that cannot be written safely to an environment file." >&2
        exit 1
    fi

    printf "%s='%s'\n" "${name}" "${value}"
}

require_absolute_path "PI_CC_DATA" "${DATA_DIR}"
require_absolute_path "PI_CC_INSTALL_ROOT" "${INSTALL_ROOT}"
require_absolute_path "PI_CC_ADMIN_PASSWORD_FILE" "${HASH_FILE}"
require_absolute_path "PI_CC_ADMIN_PASSWORD_ONCE_FILE" "${PLAIN_FILE}"
require_absolute_path "PI_CC_APPROVED_ROOT" "${APPROVED_ROOT}"
require_absolute_path "DEVFLEET_CLAUDE_CREDENTIAL_PATH" "${CLAUDE_CREDENTIAL_PATH}"

if [[ ! "${PORT}" =~ ^[0-9]+$ ]] || (( PORT < 1 || PORT > 65535 )); then
    echo "PI_CC_PORT must be an integer from 1 through 65535." >&2
    exit 1
fi

if [[ "${BIND_ADDRESS}" == "0.0.0.0" || "${BIND_ADDRESS}" == "::" ]]; then
    echo "DEVFLEET_BIND_ADDRESS must select one specific host address, not a wildcard." >&2
    exit 1
fi

URL_HOST="${BIND_ADDRESS}"
if [[ "${BIND_ADDRESS}" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    IFS=. read -r -a address_octets <<< "${BIND_ADDRESS}"
    for octet in "${address_octets[@]}"; do
        if (( 10#${octet} > 255 )); then
            echo "DEVFLEET_BIND_ADDRESS is not a valid IPv4 address: ${BIND_ADDRESS}" >&2
            exit 1
        fi
    done
elif [[ "${BIND_ADDRESS}" == *:* && "${BIND_ADDRESS}" =~ ^[0-9A-Fa-f:]+(%[0-9A-Za-z_.-]+)?$ ]]; then
    URL_HOST="[${BIND_ADDRESS}]"
else
    echo "DEVFLEET_BIND_ADDRESS must be a specific IPv4 or IPv6 address." >&2
    exit 1
fi

NODE_EXECUTABLE="$(resolve_executable "Node.js" "${DEVFLEET_NODE_EXECUTABLE:-node}")"
CLAUDE_EXECUTABLE="$(resolve_optional_executable "Claude Code" "${DEVFLEET_CLAUDE_EXECUTABLE:-claude}")"
ANTIGRAVITY_EXECUTABLE="$(resolve_optional_executable "Antigravity" "${DEVFLEET_ANTIGRAVITY_EXECUTABLE:-agy}")"
MUSE_EXECUTABLE="$(resolve_optional_executable "Muse Code" "${DEVFLEET_MUSE_EXECUTABLE:-muse}")"

BASE_URL="http://${URL_HOST}:${PORT}"
WORKER_PATH="${INSTALL_ROOT}/runtime/pi-worker/src/index.ts"
USAGE_SCRIPT_PATH="${INSTALL_ROOT}/runtime/pi-worker/src/usage.ts"
LISTEN_URLS="${BASE_URL}"
NODE_CONTROL_PLANE_URL="${BASE_URL}"
if [[ "${BIND_ADDRESS}" != "127.0.0.1" ]]; then
    LISTEN_URLS="http://127.0.0.1:${PORT};${BASE_URL}"
    NODE_CONTROL_PLANE_URL="http://127.0.0.1:${PORT}"
fi

umask 077
mkdir -p "${DATA_DIR}" "${NODE_CREDENTIAL_DIR}" "${DATA_PROTECTION_DIR}" "${PI_AGENT_DIR}"
chmod 0700 "${DATA_DIR}" "${NODE_CREDENTIAL_DIR}" "${DATA_PROTECTION_DIR}" "${PI_AGENT_DIR}"

export Admin__Username="${ADMIN_USER}"
export Admin__PasswordFile="${HASH_FILE}"
export NodeAuthentication__CredentialDirectory="${NODE_CREDENTIAL_DIR}"
export NodeAuthentication__CredentialFile="${NODE_FILE}"
export NodeAuthentication__Header="${NodeAuthentication__Header:-Authorization}"
export NodeAuthentication__Scheme="${NodeAuthentication__Scheme:-Bearer}"

if [[ -f "${HASH_FILE}" && -f "${NODE_ID_FILE}" && -f "${NODE_FILE}" ]]; then
    node_id="$(<"${NODE_ID_FILE}")"
    node_token="$(<"${NODE_FILE}")"
    if [[ "$(wc -c < "${NODE_ID_FILE}")" -ne 36
        || ! "${node_id}" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]; then
        echo "Node identity file must contain exactly one lowercase GUID: ${NODE_ID_FILE}" >&2
        exit 1
    fi
    if [[ "$(wc -c < "${NODE_FILE}")" -ne 64 || ! "${node_token}" =~ ^[0-9A-Fa-f]{64}$ ]]; then
        echo "Node credential file must contain exactly one 256-bit hex token: ${NODE_FILE}" >&2
        exit 1
    fi

    CONTROL_PLANE_NODE_FILE="${NODE_CREDENTIAL_DIR}/${node_id}.token"
    if [[ ! -f "${CONTROL_PLANE_NODE_FILE}" ]]; then
        echo "Refusing to replace partial authentication state." >&2
        echo "Missing control-plane credential mirror: ${CONTROL_PLANE_NODE_FILE}" >&2
        exit 1
    fi

    control_plane_node_token="$(<"${CONTROL_PLANE_NODE_FILE}")"
    if [[ "$(wc -c < "${CONTROL_PLANE_NODE_FILE}")" -ne 64
        || "${control_plane_node_token}" != "${node_token}" ]]; then
        echo "Control-plane credential does not exactly match ${NODE_FILE}: ${CONTROL_PLANE_NODE_FILE}" >&2
        exit 1
    fi

    chmod 0600 "${HASH_FILE}" "${NODE_ID_FILE}" "${NODE_FILE}" "${CONTROL_PLANE_NODE_FILE}"
elif [[ ! -e "${HASH_FILE}" && ! -e "${NODE_ID_FILE}" && ! -e "${NODE_FILE}"
    && ! -e "${PLAIN_FILE}" ]] && ! compgen -G "${NODE_CREDENTIAL_DIR}/*" > /dev/null; then
    IFS= read -r node_id < /proc/sys/kernel/random/uuid
    node_token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
    CONTROL_PLANE_NODE_FILE="${NODE_CREDENTIAL_DIR}/${node_id}.token"

    printf '%s' "${node_id}" > "${NODE_ID_FILE}"
    printf '%s' "${node_token}" > "${NODE_FILE}"
    printf '%s' "${node_token}" > "${CONTROL_PLANE_NODE_FILE}"
    chmod 0600 "${NODE_ID_FILE}" "${NODE_FILE}" "${CONTROL_PLANE_NODE_FILE}"

    setup_out="$(
        cd "${REPO_ROOT}"
        dotnet run --project src/PiCommandCenter.ControlPlane --no-launch-profile -- --setup
    )"
    password="$(printf '%s\n' "${setup_out}" | sed -n 's/^Password: //p' | head -n1)"
    if [[ -z "${password}" ]]; then
        echo "Control plane --setup did not print a one-time password." >&2
        printf '%s\n' "${setup_out}" >&2
        exit 1
    fi
    printf '%s' "${password}" > "${PLAIN_FILE}"
    chmod 0600 "${HASH_FILE}" "${PLAIN_FILE}"
else
    echo "Refusing to replace partial authentication state." >&2
    echo "${HASH_FILE}, ${NODE_ID_FILE}, ${NODE_FILE}, and its control-plane credential mirror must all exist, or all authentication state must be absent." >&2
    exit 1
fi

export Node__Id="${node_id}"

if [[ -f "${PLAIN_FILE}" ]]; then
    chmod 0600 "${PLAIN_FILE}"
fi

{
    echo "# Generated by scripts/setup-local.sh; source this file, but do not commit it."
    write_environment_value "Admin__Username" "${ADMIN_USER}"
    write_environment_value "Admin__PasswordFile" "${HASH_FILE}"
    write_environment_value "NodeAuthentication__CredentialDirectory" "${NODE_CREDENTIAL_DIR}"
    write_environment_value "NodeAuthentication__CredentialFile" "${NODE_FILE}"
    write_environment_value "NodeAuthentication__Header" "${NodeAuthentication__Header}"
    write_environment_value "NodeAuthentication__Scheme" "${NodeAuthentication__Scheme}"
    write_environment_value "Node__Id" "${node_id}"
    echo "export NodeAuthentication__CredentialDirectory Node__Id"
    write_environment_value "PI_CC_ADMIN_PASSWORD_ONCE_FILE" "${PLAIN_FILE}"
} > "${DATA_DIR}/local.env"
chmod 0600 "${DATA_DIR}/local.env"

{
    echo "# Generated by scripts/setup-local.sh; loaded by the systemd user units. Do not commit."
    write_environment_value "PATH" "${SERVICE_PATH}"
    write_environment_value "ASPNETCORE_ENVIRONMENT" "Production"
    write_environment_value "DOTNET_ENVIRONMENT" "Production"
    write_environment_value "ASPNETCORE_URLS" "${LISTEN_URLS}"
    write_environment_value "AllowedHosts" "${BIND_ADDRESS};localhost;127.0.0.1"
    write_environment_value "Admin__Username" "${ADMIN_USER}"
    write_environment_value "Admin__PasswordFile" "${HASH_FILE}"
    write_environment_value "NodeAuthentication__CredentialDirectory" "${NODE_CREDENTIAL_DIR}"
    write_environment_value "NodeAuthentication__CredentialFile" "${NODE_FILE}"
    write_environment_value "NodeAuthentication__Header" "${NodeAuthentication__Header}"
    write_environment_value "NodeAuthentication__Scheme" "${NodeAuthentication__Scheme}"
    write_environment_value "DataProtection__KeysDirectory" "${DATA_PROTECTION_DIR}"
    write_environment_value "ControlPlane__BaseUrl" "${BASE_URL}"
    write_environment_value "ConnectionStrings__ControlPlane" "Data Source=${DATA_DIR}/controlplane.db;Cache=Shared"
    write_environment_value "Node__ControlPlaneUrl" "${NODE_CONTROL_PLANE_URL}"
    write_environment_value "Node__Id" "${node_id}"
    write_environment_value "Node__DisplayName" "${NODE_DISPLAY_NAME}"
    write_environment_value "Node__EventSpoolPath" "${DATA_DIR}/node-spool.db"
    write_environment_value "Projects__ApprovedRoots__0" "${APPROVED_ROOT}"
    write_environment_value "Pi__AgentDataDirectory" "${PI_AGENT_DIR}"
    write_environment_value "Pi__WorkerPath" "${WORKER_PATH}"
    write_environment_value "Pi__NodeExecutable" "${NODE_EXECUTABLE}"
    write_environment_value "SubscriptionUsage__NodeExecutable" "${NODE_EXECUTABLE}"
    write_environment_value "SubscriptionUsage__ScriptPath" "${USAGE_SCRIPT_PATH}"
    write_environment_value "SubscriptionUsage__ClaudeCredentialPath" "${CLAUDE_CREDENTIAL_PATH}"
    write_environment_value "Claude__Executable" "${CLAUDE_EXECUTABLE}"
    write_environment_value "Antigravity__Executable" "${ANTIGRAVITY_EXECUTABLE}"
    write_environment_value "Muse__Executable" "${MUSE_EXECUTABLE}"
} > "${DATA_DIR}/pi-command-center.env"
chmod 0600 "${DATA_DIR}/pi-command-center.env"

echo "Setup complete."
echo "  data dir:        ${DATA_DIR} (0700)"
echo "  admin user:      ${ADMIN_USER}"
echo "  admin hash:      ${HASH_FILE} (0600)"
echo "  node id:         ${NODE_ID_FILE} (${node_id}, 0600)"
echo "  node credential: ${NODE_FILE} (0600)"
echo "  credential dir:  ${NODE_CREDENTIAL_DIR} (0700)"
if [[ -f "${PLAIN_FILE}" ]]; then
    echo "  one-time password file (0600): ${PLAIN_FILE}"
fi
echo "  service env:     ${DATA_DIR}/pi-command-center.env"
