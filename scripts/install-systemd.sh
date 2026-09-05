#!/usr/bin/env bash
# Publish trusted runtime files outside approved repositories, then install the
# Fedora systemd user units. Re-run after upgrading the Command Center.
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
INSTALL_ROOT="${PI_CC_INSTALL_ROOT:-${HOME}/.local/lib/pi-command-center}"
SYSTEMD_ROOT="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"

case "${INSTALL_ROOT}" in
    "${HOME}/.local/lib/pi-command-center"|"${HOME}/.local/lib/pi-command-center/"*) ;;
    *)
        echo "PI_CC_INSTALL_ROOT must be inside ~/.local/lib/pi-command-center" >&2
        exit 1
        ;;
esac

"${SCRIPT_DIR}/setup-local.sh"

install -d -m 0700 "${INSTALL_ROOT}" "${SYSTEMD_ROOT}"
rm -rf -- "${INSTALL_ROOT}/control-plane" "${INSTALL_ROOT}/node" "${INSTALL_ROOT}/runtime"
install -d -m 0700 \
    "${INSTALL_ROOT}/control-plane" \
    "${INSTALL_ROOT}/node" \
    "${INSTALL_ROOT}/runtime/pi-worker"

dotnet publish "${REPO_ROOT}/src/PiCommandCenter.ControlPlane/PiCommandCenter.ControlPlane.csproj" \
    -c Release --no-self-contained -o "${INSTALL_ROOT}/control-plane"
dotnet publish "${REPO_ROOT}/src/PiCommandCenter.Node/PiCommandCenter.Node.csproj" \
    -c Release --no-self-contained -o "${INSTALL_ROOT}/node"

install -m 0600 "${REPO_ROOT}/runtime/package.json" "${INSTALL_ROOT}/runtime/package.json"
install -m 0600 "${REPO_ROOT}/runtime/package-lock.json" "${INSTALL_ROOT}/runtime/package-lock.json"
cp -a -- "${REPO_ROOT}/runtime/pi-worker/." "${INSTALL_ROOT}/runtime/pi-worker/"
(
    cd "${INSTALL_ROOT}/runtime"
    npm ci --omit=dev --ignore-scripts
)
chmod -R u=rwX,go= "${INSTALL_ROOT}"

install -m 0644 "${REPO_ROOT}/deploy/systemd/pi-command-center-control-plane.service" "${SYSTEMD_ROOT}/"
install -m 0644 "${REPO_ROOT}/deploy/systemd/pi-command-center-node.service" "${SYSTEMD_ROOT}/"
systemctl --user daemon-reload

echo "Installed protected runtime under ${INSTALL_ROOT}."
echo "Start with: systemctl --user enable --now pi-command-center-control-plane.service pi-command-center-node.service"
