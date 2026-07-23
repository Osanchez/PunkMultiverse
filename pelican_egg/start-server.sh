#!/bin/bash
# PUNK Multiverse dedicated coordinator launcher (runs inside the Wine container).
#
# Responsibilities:
#   1. Locate the operator-supplied game install (Punk.exe + BepInEx + the mod).
#   2. Derive a headless server config.cfg from the environment (port, gameplay tunables, ...),
#      starting from the mod's shipped config.default.cfg so unset keys keep sane defaults.
#   3. Prevent the base game from relaunching through Steam (steam_appid.txt).
#   4. Boot the game headless under Wine as a coordinator on the Udp transport, streaming the
#      BepInEx log to stdout so the panel sees the readiness line and console.
#   5. On SIGTERM/SIGINT (panel "Stop"), give the game a moment to flush its economy save,
#      then force Wine down.
#
# Everything is driven by env vars (documented in README.md / the egg). Nothing here needs
# Steam to be running.
set -uo pipefail

# --------------------------------------------------------------------- configuration (env)
GAME_DIR="${GAME_DIR:-/home/container}"
STARTUP_EXE="${STARTUP_EXE:-Punk.exe}"
SERVER_PORT="${SERVER_PORT:-7778}"                    # Pelican's primary allocation
SERVER_ADDRESS="${SERVER_ADDRESS:-0.0.0.0}"           # advertised join host (informational)
STEAM_APPID="${STEAM_APPID:-2850470}"                 # PUNK Playtest appid (steam_appid.txt)

AUTO_START_RUN="${AUTO_START_RUN:-0}"                 # 1 = auto-launch the run; 0 = first player (admin) presses START
SYNC_DIAGNOSTICS="${SYNC_DIAGNOSTICS:-0}"             # 1 = verbose [Diag] logging
ENABLE_ADMIN_COMMANDS="${ENABLE_ADMIN_COMMANDS:-1}"   # 1 = watch devcmd.txt so ops can inject devcmds
MOD_MANIFEST_POLICY="${MOD_MANIFEST_POLICY:-Reject}"  # Reject | Warn — version-mismatch gate for joiners
HP_SCALING_PER_PLAYER="${HP_SCALING_PER_PLAYER:-0.25}"
COIN_DESPAWN_SECONDS="${COIN_DESPAWN_SECONDS:-45}"

STOP_GRACE_SECONDS="${STOP_GRACE_SECONDS:-20}"        # shutdown: seconds to wait for a clean exit before wineserver -k
WINEDEBUG="${WINEDEBUG:--all}"
EXTRA_ARGS="${EXTRA_ARGS:-}"                          # extra Unity/Punk.exe args
export WINEDEBUG WINEPREFIX="${WINEPREFIX:-/home/container/.wine}" DISPLAY="${DISPLAY:-:0}"

PLUGIN_DIR="${GAME_DIR}/BepInEx/plugins/PunkMultiverse"
CFG="${PLUGIN_DIR}/config.cfg"
CFG_DEFAULT="${PLUGIN_DIR}/config.default.cfg"
BEPINEX_LOG="${GAME_DIR}/BepInEx/LogOutput.log"

log() { echo "[server] $*"; }
fail() { echo "[server][FATAL] $*" >&2; exit 1; }

# --------------------------------------------------------------------- preflight
cd "${GAME_DIR}" || fail "GAME_DIR '${GAME_DIR}' does not exist"

if [[ ! -f "${GAME_DIR}/${STARTUP_EXE}" ]]; then
    echo "======================================================================"
    echo " Game files not found: ${GAME_DIR}/${STARTUP_EXE} is missing."
    echo ""
    echo " PUNK is a Steam playtest and cannot be fetched by SteamCMD. Upload your"
    echo " modded install (the folder containing Punk.exe, BepInEx/, Punk_Data/, and"
    echo " the winhttp.dll doorstop) to the server root, then restart."
    echo " See pelican_egg/README.md for the exact file list."
    echo "======================================================================"
    fail "no game executable"
fi
[[ -d "${GAME_DIR}/BepInEx" ]] || fail "BepInEx/ not found — the mod is not installed in this game copy"

# --------------------------------------------------------------------- config.cfg from env
mkdir -p "${PLUGIN_DIR}"

# Section-aware setter: replace `Key = ...` inside `[Section]`, else insert it under that
# section (creating the section if absent). Keeps the rest of the file untouched.
set_cfg() {
    local sec="$1" key="$2" val="$3" file="$4"
    awk -v sec="[$sec]" -v key="$key" -v val="$val" '
        BEGIN { insec=0; done=0; seensec=0 }
        {
            if ($0 == sec)                          { insec=1; seensec=1; print; next }
            if (insec && $0 ~ /^\[/) { if (!done) { print key" = "val; done=1 } insec=0 }
            if (insec && $0 ~ ("^[[:space:]]*" key "[[:space:]]*=")) { print key" = "val; done=1; insec=0; next }
            print
        }
        END {
            if (!done) {
                if (!seensec) print sec
                print key" = "val
            }
        }
    ' "$file" > "$file.tmp" && mv "$file.tmp" "$file"
}

# Start from the shipped player defaults when present (keeps every unrelated key sane), else
# from any existing config, else an empty file the setter will populate section by section.
if [[ -f "${CFG_DEFAULT}" ]]; then
    log "seeding config.cfg from config.default.cfg"
    cp -f "${CFG_DEFAULT}" "${CFG}"
elif [[ ! -f "${CFG}" ]]; then
    : > "${CFG}"
fi

bool() { [[ "${1}" == "1" || "${1,,}" == "true" ]] && echo "true" || echo "false"; }

# Networking: force the direct-UDP transport on the allocated port. CoordinatorMode is also
# set via PUNKMV_COORDINATOR below; setting the config key too makes the file self-describing.
set_cfg "Transport" "Transport"        "Udp"                       "${CFG}"
set_cfg "Transport" "UdpPort"          "${SERVER_PORT}"            "${CFG}"
set_cfg "Transport" "UdpAddress"       "${SERVER_ADDRESS}"        "${CFG}"
set_cfg "Transport" "SteamAppId"       "${STEAM_APPID}"           "${CFG}"
# Coordinator / session behavior.
set_cfg "Session"   "CoordinatorMode"  "true"                      "${CFG}"
set_cfg "Session"   "ModManifestPolicy" "${MOD_MANIFEST_POLICY}"   "${CFG}"
set_cfg "Session"   "EnemyHealthScalePerPlayer" "${HP_SCALING_PER_PLAYER}" "${CFG}"
set_cfg "Session"   "CoinDespawnSeconds" "${COIN_DESPAWN_SECONDS}" "${CFG}"
# Debug/automation knobs the coordinator honors.
set_cfg "Debug"     "AutoLaunchRun"    "$(bool "${AUTO_START_RUN}")" "${CFG}"
set_cfg "Debug"     "SyncDiagnostics"  "$(bool "${SYNC_DIAGNOSTICS}")" "${CFG}"
if [[ "${ENABLE_ADMIN_COMMANDS}" == "1" || "${ENABLE_ADMIN_COMMANDS,,}" == "true" ]]; then
    set_cfg "Debug" "CommandFile" "devcmd.txt" "${CFG}"
    : > "${PLUGIN_DIR}/devcmd.txt"
else
    set_cfg "Debug" "CommandFile" "" "${CFG}"
fi

log "config.cfg written (Transport=Udp UdpPort=${SERVER_PORT} coordinator=1 autoLaunch=$(bool "${AUTO_START_RUN}"))"

# Keep the base game from bouncing through the Steam client on launch.
echo "${STEAM_APPID}" > "${GAME_DIR}/steam_appid.txt"

# --------------------------------------------------------------------- Wine prefix + display
if [[ ! -f "${WINEPREFIX}/system.reg" ]]; then
    log "initializing Wine prefix at ${WINEPREFIX} (first boot)"
    wineboot --init >/dev/null 2>&1 || true
    wineserver -w
fi

# A virtual framebuffer for Unity's graphics init even under -nographics (some Unity builds
# still create a device). Harmless if the game never touches it.
if ! pgrep -x Xvfb >/dev/null 2>&1; then
    log "starting Xvfb on ${DISPLAY}"
    Xvfb "${DISPLAY}" -screen 0 320x240x16 -nolisten tcp >/dev/null 2>&1 &
    XVFB_PID=$!
fi

# --------------------------------------------------------------------- launch + log relay
: > "${BEPINEX_LOG}" 2>/dev/null || true

log "launching ${STARTUP_EXE} (coordinator, Udp:${SERVER_PORT})"
# Scrub DOORSTOP_* so the fresh process runs BepInEx injection (an inherited "already
# initialized" marker would boot a vanilla, mod-less game — the sidecar gotcha).
env -u DOORSTOP_INITIALIZED -u DOORSTOP_INVOKE_DLL_PATH -u DOORSTOP_PROCESS_PATH \
    PUNKMV_COORDINATOR=1 \
    PUNKMV_TRANSPORT=Udp \
    SteamAppId="${STEAM_APPID}" SteamGameId="${STEAM_APPID}" \
    wine "${GAME_DIR}/${STARTUP_EXE}" -batchmode -nographics \
        -logFile "${GAME_DIR}/Player.log" ${EXTRA_ARGS} &
WINE_PID=$!

# Stream the BepInEx log to stdout so the panel's console + done-regex ([Udp] hosting on) work.
( tail -n +1 -F "${BEPINEX_LOG}" 2>/dev/null ) &
TAIL_PID=$!

# --------------------------------------------------------------------- graceful shutdown
shutdown() {
    log "stop requested — signaling the game to exit (grace ${STOP_GRACE_SECONDS}s)"
    # Signal Wine and let Unity's OnApplicationQuit run the mod's teardown (StopSession →
    # EconomyStash.Save). We deliberately do NOT push a devcmd here: a network-bound verb could
    # hang the stop. A dedicated in-mod `quit` verb (save + Application.Quit) is the clean
    # follow-up if teardown-on-signal proves unreliable under Wine.
    kill -TERM "${WINE_PID}" 2>/dev/null || true
    for _ in $(seq 1 "${STOP_GRACE_SECONDS}"); do
        kill -0 "${WINE_PID}" 2>/dev/null || break
        sleep 1
    done
    wineserver -k 2>/dev/null || true
    kill "${TAIL_PID}" 2>/dev/null || true
    [[ -n "${XVFB_PID:-}" ]] && kill "${XVFB_PID}" 2>/dev/null || true
    log "coordinator stopped"
    exit 0
}
trap shutdown SIGTERM SIGINT

# Wait on the game; if it dies on its own, tear the rest down and surface its exit code.
wait "${WINE_PID}"
CODE=$?
log "game process exited (code ${CODE})"
kill "${TAIL_PID}" 2>/dev/null || true
[[ -n "${XVFB_PID:-}" ]] && kill "${XVFB_PID}" 2>/dev/null || true
wineserver -k 2>/dev/null || true
exit "${CODE}"
