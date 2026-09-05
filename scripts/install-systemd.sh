#!/usr/bin/env bash
# Publish the native runtime into the protected per-user install root, then
# install and restart the Fedora systemd user services.
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
DATA_ROOT="${HOME}/.local/share/devfleet"
INSTALL_ROOT="${HOME}/.local/lib/devfleet"
INSTALL_PARENT="${HOME}/.local/lib"
SYSTEMD_ROOT="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
STAGING_ROOT=""
BACKUP_ROOT=""
restore_previous() {
    if [[ -z "${BACKUP_ROOT}" || ! -d "${BACKUP_ROOT}" ]]; then
        return
    fi
    echo "Restoring the previous runtime from ${BACKUP_ROOT}." >&2
    if ! rm -rf -- "${INSTALL_ROOT}"; then
        echo "Failed to remove the replacement runtime; backup retained at ${BACKUP_ROOT}." >&2
        return 1
    fi
    if ! mv -- "${BACKUP_ROOT}" "${INSTALL_ROOT}"; then
        echo "Failed to restore the previous runtime; backup retained at ${BACKUP_ROOT}." >&2
        return 1
    fi
    BACKUP_ROOT=""
    systemctl --user daemon-reload || true
    systemctl --user restart \
        pi-command-center-control-plane.service \
        pi-command-center-node.service || true
}

cleanup() {
    local exit_status=$?
    if [[ -n "${STAGING_ROOT}" && -d "${STAGING_ROOT}" ]]; then
        rm -rf -- "${STAGING_ROOT}" || true
    fi
    if [[ -n "${BACKUP_ROOT}" && -d "${BACKUP_ROOT}" ]]; then
        restore_previous || true
    fi
    return "${exit_status}"
}
trap cleanup EXIT

for executable in dotnet npm node git bwrap curl systemctl; do
    if ! command -v -- "${executable}" >/dev/null 2>&1; then
        echo "Required executable not found on PATH: ${executable}" >&2
        exit 1
    fi
done

if [[ ! -x /usr/bin/dotnet ]]; then
    echo "The systemd units require the Fedora .NET host at /usr/bin/dotnet." >&2
    exit 1
fi

PI_CC_DATA="${DATA_ROOT}" PI_CC_INSTALL_ROOT="${INSTALL_ROOT}" "${SCRIPT_DIR}/setup-local.sh"

install -d -m 0700 "${INSTALL_PARENT}" "${SYSTEMD_ROOT}"
STAGING_ROOT="$(mktemp -d "${INSTALL_PARENT}/.devfleet.install.XXXXXX")"
install -d -m 0700 \
    "${STAGING_ROOT}/control-plane" \
    "${STAGING_ROOT}/node" \
    "${STAGING_ROOT}/runtime/pi-worker/src"

dotnet publish "${REPO_ROOT}/src/PiCommandCenter.ControlPlane/PiCommandCenter.ControlPlane.csproj" \
    -c Release --no-self-contained -o "${STAGING_ROOT}/control-plane"
dotnet publish "${REPO_ROOT}/src/PiCommandCenter.Node/PiCommandCenter.Node.csproj" \
    -c Release --no-self-contained -o "${STAGING_ROOT}/node"

install -m 0600 "${REPO_ROOT}/runtime/package.json" "${STAGING_ROOT}/runtime/package.json"
install -m 0600 "${REPO_ROOT}/runtime/package-lock.json" "${STAGING_ROOT}/runtime/package-lock.json"
cp -a -- "${REPO_ROOT}/runtime/pi-worker/src/." "${STAGING_ROOT}/runtime/pi-worker/src/"
npm ci --prefix "${STAGING_ROOT}/runtime" --omit=dev --ignore-scripts
chmod -R u=rwX,go= "${STAGING_ROOT}"

if [[ -e "${INSTALL_ROOT}" ]]; then
    BACKUP_ROOT="${INSTALL_PARENT}/.devfleet.previous.$$"
    if [[ -e "${BACKUP_ROOT}" ]]; then
        echo "Refusing to overwrite unexpected deployment backup: ${BACKUP_ROOT}" >&2
        exit 1
    fi
    mv -- "${INSTALL_ROOT}" "${BACKUP_ROOT}"
fi

if ! mv -- "${STAGING_ROOT}" "${INSTALL_ROOT}"; then
    echo "Failed to activate the published runtime." >&2
    exit 1
fi
STAGING_ROOT=""


install -m 0644 "${REPO_ROOT}/deploy/systemd/pi-command-center-control-plane.service" "${SYSTEMD_ROOT}/"
install -m 0644 "${REPO_ROOT}/deploy/systemd/pi-command-center-node.service" "${SYSTEMD_ROOT}/"

if ! systemctl --user daemon-reload ||
    ! systemctl --user enable \
        pi-command-center-control-plane.service \
        pi-command-center-node.service ||
    ! systemctl --user restart \
        pi-command-center-control-plane.service \
        pi-command-center-node.service; then
    echo "Failed to activate the DevFleet services." >&2
    restore_previous
    exit 1
fi
HEALTH_URL="http://127.0.0.1:${PI_CC_PORT:-5057}/health"
ready=false
for _ in {1..60}; do
    if systemctl --user is-active --quiet pi-command-center-control-plane.service &&
        systemctl --user is-active --quiet pi-command-center-node.service &&
        curl --fail --silent "${HEALTH_URL}" >/dev/null; then
        ready=true
        break
    fi
    if ! systemctl --user is-active --quiet pi-command-center-control-plane.service ||
        ! systemctl --user is-active --quiet pi-command-center-node.service; then
        break
    fi
    sleep 0.5
done

if [[ "${ready}" != true ]]; then
    echo "DevFleet services did not become ready at ${HEALTH_URL}." >&2
    systemctl --user --no-pager status \
        pi-command-center-control-plane.service \
        pi-command-center-node.service >&2 || true
    restore_previous
    exit 1
fi

if [[ -n "${BACKUP_ROOT}" ]]; then
    rm -rf -- "${BACKUP_ROOT}"
    BACKUP_ROOT=""
fi

echo "Installed protected runtime under ${INSTALL_ROOT}."
echo "Persistent state remains under ${DATA_ROOT}."
