#!/bin/bash
# Build (and optionally push) the PUNK Multiverse server image.
#
# It stages the redistributable BepInEx loader (Doorstop winhttp.dll proxy + BepInEx/core) from a
# known-good game install into _bepinex_stage/ so the Dockerfile can bake it in. The base game and
# the mod are NOT baked: the base game is operator-supplied (it's a Steam playtest), and the mod is
# pulled fresh from GitHub releases at runtime.
#
# Usage (run in Linux / WSL where docker works):
#   ./build-image.sh                 # build only
#   ./build-image.sh --push          # build then push
#   GAME_DIR=/path/to/PUNK ./build-image.sh
#   IMAGE=osanchezdev/punk-punkmultiverse:latest ./build-image.sh --push
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
# Default game dir: the install two levels up (…/PUNK Playtest) — pelican_egg sits in the repo,
# the repo sits in the game root.
GAME_DIR="${GAME_DIR:-$(cd "${HERE}/../.." && pwd)}"
IMAGE="${IMAGE:-osanchezdev/punk-punkmultiverse:latest}"
STAGE="${HERE}/_bepinex_stage"

echo "==> game install : ${GAME_DIR}"
echo "==> image tag     : ${IMAGE}"

# --- stage the BepInEx loader ------------------------------------------------------------------
for f in winhttp.dll doorstop_config.ini .doorstop_version; do
    [[ -f "${GAME_DIR}/${f}" ]] || { echo "ERROR: ${GAME_DIR}/${f} not found — is this a BepInEx install?"; exit 1; }
done
[[ -d "${GAME_DIR}/BepInEx/core" ]] || { echo "ERROR: ${GAME_DIR}/BepInEx/core not found"; exit 1; }

echo "==> staging BepInEx loader into _bepinex_stage/"
rm -rf "${STAGE}"
mkdir -p "${STAGE}/BepInEx"
cp -f  "${GAME_DIR}/winhttp.dll"         "${STAGE}/winhttp.dll"
cp -f  "${GAME_DIR}/doorstop_config.ini" "${STAGE}/doorstop_config.ini"
cp -f  "${GAME_DIR}/.doorstop_version"   "${STAGE}/.doorstop_version"
cp -rf "${GAME_DIR}/BepInEx/core"        "${STAGE}/BepInEx/core"
echo "    staged loader: $(du -sh "${STAGE}" | cut -f1)"

# --- stage the base game (baked into the image; NOT the mod, NOT BepInEx) -----------------------
GAME_STAGE="${HERE}/_game_stage"
echo "==> staging base game into _game_stage/"
for f in Punk.exe UnityPlayer.dll; do
    [[ -f "${GAME_DIR}/${f}" ]] || { echo "ERROR: ${GAME_DIR}/${f} not found"; exit 1; }
done
for d in Punk_Data MonoBleedingEdge; do
    [[ -d "${GAME_DIR}/${d}" ]] || { echo "ERROR: ${GAME_DIR}/${d} not found"; exit 1; }
done
rm -rf "${GAME_STAGE}"
mkdir -p "${GAME_STAGE}"
cp -f  "${GAME_DIR}/Punk.exe"        "${GAME_STAGE}/Punk.exe"
cp -f  "${GAME_DIR}/UnityPlayer.dll" "${GAME_STAGE}/UnityPlayer.dll"
cp -rf "${GAME_DIR}/Punk_Data"       "${GAME_STAGE}/Punk_Data"
cp -rf "${GAME_DIR}/MonoBleedingEdge" "${GAME_STAGE}/MonoBleedingEdge"
echo "    staged game: $(du -sh "${GAME_STAGE}" | cut -f1)"

# --- build -------------------------------------------------------------------------------------
echo "==> docker build"
docker build -t "${IMAGE}" "${HERE}"

if [[ "${1:-}" == "--push" ]]; then
    echo "==> docker push ${IMAGE}"
    docker push "${IMAGE}"
fi

echo "==> done."
