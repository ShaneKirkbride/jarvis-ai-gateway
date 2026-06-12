#!/usr/bin/env bash
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-/root/.dotnet}"
DOTNET_VERSION="${DOTNET_VERSION:-10.0.301}"
DOTNET_BIN="${DOTNET_ROOT}/dotnet"
DOTNET_SYMLINK="/usr/local/bin/dotnet"
DOTNET_INSTALL_SCRIPT="/tmp/dotnet-install.sh"

install_dotnet() {
  mkdir -p "${DOTNET_ROOT}"

  if [ -x "${DOTNET_BIN}" ] && [ "$("${DOTNET_BIN}" --version)" = "${DOTNET_VERSION}" ]; then
    echo ".NET SDK ${DOTNET_VERSION} is already installed at ${DOTNET_BIN}."
    return
  fi

  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${DOTNET_INSTALL_SCRIPT}"
  elif command -v wget >/dev/null 2>&1; then
    wget -q https://dot.net/v1/dotnet-install.sh -O "${DOTNET_INSTALL_SCRIPT}"
  else
    echo "Either curl or wget is required to download dotnet-install.sh." >&2
    exit 1
  fi

  chmod +x "${DOTNET_INSTALL_SCRIPT}"
  "${DOTNET_INSTALL_SCRIPT}" --version "${DOTNET_VERSION}" --install-dir "${DOTNET_ROOT}" --no-path
}

install_dotnet

if [ ! -x "${DOTNET_BIN}" ]; then
  echo "Expected dotnet CLI at ${DOTNET_BIN}, but it is missing or not executable." >&2
  exit 1
fi

mkdir -p "$(dirname "${DOTNET_SYMLINK}")"
ln -sfn "${DOTNET_BIN}" "${DOTNET_SYMLINK}"

command -v dotnet
dotnet --version
dotnet --info
