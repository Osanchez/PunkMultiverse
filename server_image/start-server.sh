#!/bin/bash
# PUNK Multiverse dedicated coordinator launcher (runs inside the Wine container).
#
# Responsibilities:
#   1. Locate the operator-supplied base game (Punk.exe).
#   2. Overlay the image's baked BepInEx loader onto the game so even a vanilla copy is mod-ready.
#   3. Self-update: pull the latest (or pinned) mod plugin from GitHub releases.
#   4. Derive a headless server config.cfg from the environment (port, gameplay tunables, ...).
#   5. Prevent the base game from relaunching through Steam (steam_appid.txt).
#   6. Boot the game headless under Wine as a coordinator on the Udp transport, streaming the
#      BepInEx log to stdout so `docker logs` shows the readiness line and console.
#   7. On SIGTERM/SIGINT (`docker stop`), drive the mod's `quit` (save + notify) then force Wine.
#
# Everything is driven by env vars (documented in README.md / the egg). Only the base game is
# operator-supplied; BepInEx and the mod come from the image + GitHub. Nothing needs Steam.
set -uo pipefail

# --------------------------------------------------------------------- configuration (env)
GAME_DIR="${GAME_DIR:-/home/container}"
# ---------------------------------------------------------------------------------------------
# WHICH KNOBS DID THE OPERATOR ACTUALLY SET?
#
# Captured HERE, before the "${VAR:-default}" lines below, because those assign the variable and
# destroy the distinction between "the operator chose 45" and "nobody said anything so the script
# invented 45". That distinction matters: this script rewrites config.cfg on EVERY boot, so a value
# it invents silently overwrites whatever an operator edited into the persisted config — which is
# exactly how a hand-tuned Battle Royale schedule kept reverting to the shipped defaults after every
# restart (2026-07-28). Anything NOT listed as explicitly set is now left alone: the persisted
# config.cfg wins, and failing that the mod's own defaults do.
_EXPLICIT_VARS=""
for _v in GAME_MODE BR_MATCH_MINUTES BR_RING_STAGES \
          BR_CARE_PACKAGE_MINUTES BR_PVP_DAMAGE_SCALE BR_ENEMY_HP_SCALE \
          BR_MIN_PLAYERS; do
    eval "_isset=\${${_v}+yes}"
    [ "${_isset}" = "yes" ] && _EXPLICIT_VARS="${_EXPLICIT_VARS} ${_v}"
done
unset _v _isset

# True when the named environment variable was provided by the operator.
was_set() {
    case " ${_EXPLICIT_VARS} " in
        *" $1 "*) return 0 ;;
        *)        return 1 ;;
    esac
}

STARTUP_EXE="${STARTUP_EXE:-Punk.exe}"
SERVER_PORT="${SERVER_PORT:-7778}"                    # the published UDP port
SERVER_ADDRESS="${SERVER_ADDRESS:-0.0.0.0}"           # advertised join host (informational)
STEAM_APPID="${STEAM_APPID:-2850470}"                 # PUNK Playtest appid (steam_appid.txt)

AUTO_START_RUN="${AUTO_START_RUN:-0}"                 # 1 = auto-launch the run; 0 = first player (admin) presses START
SYNC_DIAGNOSTICS="${SYNC_DIAGNOSTICS:-0}"             # 1 = verbose [Diag] logging
LOG_LEVEL="${LOG_LEVEL:-Verbose}"                     # Normal | Verbose | Quiet — server console defaults to full instrumentation
ENABLE_ADMIN_COMMANDS="${ENABLE_ADMIN_COMMANDS:-1}"   # 1 = watch devcmd.txt so ops can inject devcmds
MOD_MANIFEST_POLICY="${MOD_MANIFEST_POLICY:-Reject}"  # Reject | Warn — version-mismatch gate for joiners
HP_SCALING_PER_PLAYER="${HP_SCALING_PER_PLAYER:-0.25}"
COIN_DESPAWN_SECONDS="${COIN_DESPAWN_SECONDS:-45}"
EMPTY_SERVER_RESET_SECONDS="${EMPTY_SERVER_RESET_SECONDS:-120}"  # all players gone -> fresh lobby after this grace (0 = never)
SERVER_FRAME_RATE_CAP="${SERVER_FRAME_RATE_CAP:-120}"            # headless frame cap (0 = uncapped; uncapped wastes CPU)

# Game mode. Defaults to the normal co-op game. Set GAME_MODE=BattleRoyale to run
# last-ship-standing matches (docs/BATTLE_ROYALE.md); the BR_* knobs only matter once that is set.
GAME_MODE="${GAME_MODE:-Standard}"                    # Standard | BattleRoyale
# These two ARE the ring schedule. Every zone's safe window and closure time are derived from them
# on a curve (long windows early, near none late) — there is no per-zone knob to set any more.
BR_MATCH_MINUTES="${BR_MATCH_MINUTES:-20}"            # total match length; the ring closes fully at this mark
BR_RING_STAGES="${BR_RING_STAGES:-12}"                # how many closures the match has
BR_CARE_PACKAGE_MINUTES="${BR_CARE_PACKAGE_MINUTES:-10}" # supply-drop interval (0 = none)
BR_PVP_DAMAGE_SCALE="${BR_PVP_DAMAGE_SCALE:-0.25}"    # player-vs-player damage multiplier
BR_ENEMY_HP_SCALE="${BR_ENEMY_HP_SCALE:-0.5}"         # enemy health multiplier (0.5 ~ double damage to enemies)
BR_MIN_PLAYERS="${BR_MIN_PLAYERS:-2}"                 # players needed to START a match

# Self-provisioning.
INSTALL_BEPINEX="${INSTALL_BEPINEX:-1}"               # 1 = overlay the image's baked BepInEx loader each boot
MOD_AUTO_UPDATE="${MOD_AUTO_UPDATE:-1}"               # 1 = check GitHub for a newer mod release on boot
MOD_VERSION="${MOD_VERSION:-latest}"                  # "latest" or a release tag like v0.1.131
MOD_RELEASE_REPO="${MOD_RELEASE_REPO:-Osanchez/PunkMultiverse}"
GITHUB_TOKEN="${GITHUB_TOKEN:-}"                      # optional: lifts API rate limits / private repos
BEPINEX_STAGE="${BEPINEX_STAGE:-/opt/bepinex}"        # where the image baked the loader
GAME_STAGE="${GAME_STAGE:-/opt/game}"                 # where the image baked the base game

STOP_GRACE_SECONDS="${STOP_GRACE_SECONDS:-20}"        # shutdown: seconds to wait for a clean exit before wineserver -k
WINEDEBUG="${WINEDEBUG:--all}"
EXTRA_ARGS="${EXTRA_ARGS:-}"                          # extra Unity/Punk.exe args
# winhttp=n,b makes Wine load the LOCAL winhttp.dll (the BepInEx/Doorstop proxy in the game dir)
# in preference to its own builtin. Without it, Doorstop never injects, BepInEx never loads
# (BepInEx/LogOutput.log stays empty), the mod/coordinator never starts, and the game just sits at
# the main menu. This is THE fix for running BepInEx games under Wine/Proton.
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:-winhttp=n,b}"
export WINEDEBUG WINEPREFIX="${WINEPREFIX:-/home/container/.wine}" DISPLAY="${DISPLAY:-:0}"

PLUGIN_DIR="${GAME_DIR}/BepInEx/plugins/PunkMultiverse"
CFG="${PLUGIN_DIR}/config.cfg"
CFG_DEFAULT="${PLUGIN_DIR}/config.default.cfg"
BEPINEX_LOG="${GAME_DIR}/BepInEx/LogOutput.log"
VERSION_MARKER="${PLUGIN_DIR}/.installed_version"

log() { echo "[server] $*"; }
fail() { echo "[server][FATAL] $*" >&2; exit 1; }

# --------------------------------------------------------------------- provision base game
cd "${GAME_DIR}" || fail "GAME_DIR '${GAME_DIR}' does not exist"

# The base game is baked into the image at ${GAME_STAGE}. Docker mounts a persistent volume over
# /home/container that would shadow anything COPY'd there in the image, so the game is copied in
# here on first boot. The volume persists, so later boots find Punk.exe and skip the copy.
if [[ ! -f "${GAME_DIR}/${STARTUP_EXE}" && -f "${GAME_STAGE}/${STARTUP_EXE}" ]]; then
    log "installing baked base game (first boot) from ${GAME_STAGE}"
    cp -a "${GAME_STAGE}/." "${GAME_DIR}/"
    log "base game installed ($(du -sh "${GAME_DIR}" 2>/dev/null | cut -f1))"
fi

if [[ ! -f "${GAME_DIR}/${STARTUP_EXE}" ]]; then
    echo "======================================================================"
    echo " Base game not found: ${GAME_DIR}/${STARTUP_EXE} is missing, and none is"
    echo " baked at ${GAME_STAGE}. This image should include the game; rebuild it with"
    echo " build-image.sh (which stages the base game from a local install)."
    echo "======================================================================"
    fail "no game executable"
fi

# --------------------------------------------------------------------- overlay BepInEx loader
# Copy the image's baked loader (Doorstop proxy + BepInEx/core) over the game. Idempotent and
# cheap (~1.5 MB); it makes a vanilla game mod-ready and keeps the loader version deterministic.
# Never touches BepInEx/config or the plugins folder beyond what the mod step manages.
if [[ "${INSTALL_BEPINEX}" == "1" || "${INSTALL_BEPINEX,,}" == "true" ]]; then
    if [[ -d "${BEPINEX_STAGE}" ]]; then
        log "overlaying baked BepInEx loader from ${BEPINEX_STAGE}"
        cp -f  "${BEPINEX_STAGE}/winhttp.dll"         "${GAME_DIR}/winhttp.dll"         2>/dev/null || true
        cp -f  "${BEPINEX_STAGE}/doorstop_config.ini" "${GAME_DIR}/doorstop_config.ini" 2>/dev/null || true
        cp -f  "${BEPINEX_STAGE}/.doorstop_version"   "${GAME_DIR}/.doorstop_version"   2>/dev/null || true
        mkdir -p "${GAME_DIR}/BepInEx"
        cp -rf "${BEPINEX_STAGE}/BepInEx/core"        "${GAME_DIR}/BepInEx/"            2>/dev/null || true
    else
        log "WARNING: no baked BepInEx at ${BEPINEX_STAGE} — relying on game copy's own loader"
    fi
fi
[[ -d "${GAME_DIR}/BepInEx/core" ]] || fail "BepInEx/core missing and none baked — cannot load the mod"

# --------------------------------------------------------------------- self-update the mod
# Pull the mod plugin (PunkMultiverse.dll + LiteNetLib.dll) from GitHub releases into the plugins
# folder. Skips the download when the wanted release is already installed; degrades to the
# existing copy when GitHub is unreachable.
update_mod() {
    [[ "${MOD_AUTO_UPDATE}" == "1" || "${MOD_AUTO_UPDATE,,}" == "true" ]] || { log "mod auto-update disabled"; return; }
    local api hdr tag asset current
    hdr=(-H "Accept: application/vnd.github+json")
    [[ -n "${GITHUB_TOKEN}" ]] && hdr+=(-H "Authorization: Bearer ${GITHUB_TOKEN}")

    if [[ "${MOD_VERSION}" == "latest" ]]; then
        api="https://api.github.com/repos/${MOD_RELEASE_REPO}/releases/latest"
    else
        api="https://api.github.com/repos/${MOD_RELEASE_REPO}/releases/tags/${MOD_VERSION}"
    fi

    local meta
    if ! meta="$(curl -fsSL "${hdr[@]}" "${api}" 2>/dev/null)"; then
        log "WARNING: could not reach GitHub (${api}); keeping the installed mod"
        return
    fi
    tag="$(echo "${meta}" | jq -r '.tag_name // empty')"
    # Prefer the PunkMultiverse-*.zip asset; fall back to the first zip.
    asset="$(echo "${meta}" | jq -r '(.assets[] | select(.name|test("PunkMultiverse.*\\.zip$")) | .browser_download_url) // (.assets[] | select(.name|test("\\.zip$")) | .browser_download_url)' | head -n1)"
    if [[ -z "${tag}" || -z "${asset}" ]]; then
        log "WARNING: no release asset found for '${MOD_VERSION}'; keeping the installed mod"
        return
    fi

    current="$(cat "${VERSION_MARKER}" 2>/dev/null || echo none)"
    if [[ "${current}" == "${tag}" && -f "${PLUGIN_DIR}/PunkMultiverse.dll" ]]; then
        log "mod up to date (${tag})"
        return
    fi

    log "updating mod ${current} -> ${tag}"
    local tmp; tmp="$(mktemp -d)"
    if ! curl -fsSL "${hdr[@]}" -o "${tmp}/mod.zip" "${asset}"; then
        log "WARNING: download failed (${asset}); keeping the installed mod"
        rm -rf "${tmp}"; return
    fi
    if ! unzip -o -q "${tmp}/mod.zip" -d "${tmp}/x"; then
        log "WARNING: release zip did not extract; keeping the installed mod"
        rm -rf "${tmp}"; return
    fi
    # The release zip lays out BepInEx/plugins/PunkMultiverse/*.dll — find that folder wherever it
    # landed and copy its contents into our plugin dir.
    local src; src="$(find "${tmp}/x" -type d -name PunkMultiverse -path '*plugins*' | head -n1)"
    [[ -z "${src}" ]] && src="$(dirname "$(find "${tmp}/x" -type f -name PunkMultiverse.dll | head -n1)" 2>/dev/null)"
    if [[ -z "${src}" || ! -f "${src}/PunkMultiverse.dll" ]]; then
        log "WARNING: PunkMultiverse.dll not in release ${tag}; keeping the installed mod"
        rm -rf "${tmp}"; return
    fi
    mkdir -p "${PLUGIN_DIR}"
    cp -f "${src}/"*.dll "${PLUGIN_DIR}/" 2>/dev/null || true
    [[ -f "${src}/config.default.cfg" ]] && cp -f "${src}/config.default.cfg" "${PLUGIN_DIR}/config.default.cfg"
    echo "${tag}" > "${VERSION_MARKER}"
    # LiteNetLib is merged INTO PunkMultiverse.dll since v0.1.164 — drop the stale standalone
    # copy an older install left behind (harmless if loaded types-wise, but confusing on disk).
    rm -f "${PLUGIN_DIR}/LiteNetLib.dll"
    log "mod ${tag} installed: $(ls -1 "${PLUGIN_DIR}"/*.dll 2>/dev/null | xargs -n1 basename 2>/dev/null | tr '\n' ' ')"
    rm -rf "${tmp}"
}
update_mod

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
set_cfg "Session"   "EmptyServerResetSeconds" "${EMPTY_SERVER_RESET_SECONDS}" "${CFG}"
set_cfg "Session"   "ServerFrameRateCap" "${SERVER_FRAME_RATE_CAP}" "${CFG}"
# Normalize GAME_MODE to the exact spelling the mod accepts. This has to happen HERE: the mod
# binds GameMode with an AcceptableValueList, so BepInEx silently rewrites anything else to
# Standard before the mod can see (or complain about) it. A typo would otherwise cost a
# whole session of the wrong ruleset with nothing in the log to explain it.
case "$(printf '%s' "${GAME_MODE}" | tr -d '_ ' | tr '[:upper:]' '[:lower:]')" in
    battleroyale) GAME_MODE="BattleRoyale" ;;
    standard|"")  GAME_MODE="Standard" ;;
    *)  log "WARNING: GAME_MODE='${GAME_MODE}' is not a recognized mode - falling back to Standard (valid: Standard, BattleRoyale)"
        GAME_MODE="Standard" ;;
esac
# Everything below is written ONLY if the operator actually set the variable. An unset knob leaves
# whatever is already in config.cfg (see the was_set block at the top of this file): a variable the
# operator actually set stays authoritative, and hand-edits survive a restart instead of being
# silently reverted to this script's defaults. NOTE: server.cfg below is applied AFTER all of this
# and beats every one of them — that is the file to reach for.
was_set GAME_MODE               && set_cfg "Session" "GameMode"             "${GAME_MODE}"             "${CFG}"
was_set BR_MATCH_MINUTES        && set_cfg "Session" "BrMatchMinutes"       "${BR_MATCH_MINUTES}"      "${CFG}"
was_set BR_RING_STAGES          && set_cfg "Session" "BrRingStages"         "${BR_RING_STAGES}"        "${CFG}"
was_set BR_CARE_PACKAGE_MINUTES && set_cfg "Session" "BrCarePackageMinutes" "${BR_CARE_PACKAGE_MINUTES}" "${CFG}"
was_set BR_PVP_DAMAGE_SCALE     && set_cfg "Session" "PvPDamageScale"       "${BR_PVP_DAMAGE_SCALE}"   "${CFG}"
was_set BR_ENEMY_HP_SCALE       && set_cfg "Session" "BrEnemyHpScale"       "${BR_ENEMY_HP_SCALE}"     "${CFG}"
was_set BR_MIN_PLAYERS          && set_cfg "Session" "BrMinPlayers"         "${BR_MIN_PLAYERS}"        "${CFG}"

# OPERATOR OVERRIDES FILE — the answer to "why isn't the container reading from a config file".
# /home/container/server.cfg lives in the volume, survives restarts and image updates, and is
# applied LAST so it beats both this script and any -e variable. One "Section.Key = Value" per line
# (Section defaults to Session), '#' comments ignored. Anything the mod binds can be set here,
# including keys this script has never heard of.
OVERRIDES_FILE="${OVERRIDES_FILE:-/home/container/server.cfg}"
if [ -f "${OVERRIDES_FILE}" ]; then
    _applied=0
    while IFS= read -r _line || [ -n "${_line}" ]; do
        case "${_line}" in ''|'#'*|';'*) continue ;; esac
        _k="${_line%%=*}"; _val="${_line#*=}"
        # trim surrounding whitespace
        _k="$(printf '%s' "${_k}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
        _val="$(printf '%s' "${_val}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
        [ -z "${_k}" ] && continue
        case "${_k}" in
            *.*) _sec="${_k%%.*}"; _key="${_k#*.}" ;;
            *)   _sec="Session";   _key="${_k}" ;;
        esac
        set_cfg "${_sec}" "${_key}" "${_val}" "${CFG}"
        _applied=$((_applied + 1))
    done < "${OVERRIDES_FILE}"
    log "applied ${_applied} override(s) from ${OVERRIDES_FILE}"
    unset _line _k _val _sec _key _applied
else
    log "no operator overrides at ${OVERRIDES_FILE} (create it to pin settings across restarts)"
fi
# Debug/automation knobs the coordinator honors.
set_cfg "Debug"     "AutoLaunchRun"    "$(bool "${AUTO_START_RUN}")" "${CFG}"
# NOTE: these two live in the mod's [Diag] section, not [Debug] — a section mismatch is
# silently ignored by BepInEx (the earlier "Debug" write of SyncDiagnostics did nothing).
set_cfg "Diag"      "SyncDiagnostics"  "$(bool "${SYNC_DIAGNOSTICS}")" "${CFG}"
set_cfg "Diag"      "LogLevel"         "${LOG_LEVEL}"              "${CFG}"
if [[ "${ENABLE_ADMIN_COMMANDS}" == "1" || "${ENABLE_ADMIN_COMMANDS,,}" == "true" ]]; then
    set_cfg "Debug" "CommandFile" "devcmd.txt" "${CFG}"
    # Clear the command file AND any crash-leftover .consuming file — a stale `quit` drained on
    # boot would make a freshly (re)started server immediately exit again (restart loop).
    : > "${PLUGIN_DIR}/devcmd.txt"
    rm -f "${PLUGIN_DIR}/devcmd.txt.consuming"
else
    set_cfg "Debug" "CommandFile" "" "${CFG}"
fi

log "config.cfg written (Transport=Udp UdpPort=${SERVER_PORT} coordinator=1 autoLaunch=$(bool "${AUTO_START_RUN}"))"
if [ "${GAME_MODE}" != "Standard" ]; then
    log "game mode: ${GAME_MODE} (match ${BR_MATCH_MINUTES}m over ${BR_RING_STAGES} ring zones, drops every ${BR_CARE_PACKAGE_MINUTES}m)"
    log "  the mod logs the derived per-zone wait/closure ladder at match start; it also reports"
    log "  any retired or misspelled config.cfg key on boot — grep [Config]"
else
    log "game mode: Standard"
fi
log "the server's own banner reports the mode it actually resolved - trust that line over this one"

# Keep the base game from bouncing through the Steam client on launch.
echo "${STEAM_APPID}" > "${GAME_DIR}/steam_appid.txt"

# --------------------------------------------------------------------- Wine prefix + display
if [[ ! -f "${WINEPREFIX}/system.reg" ]]; then
    log "initializing Wine prefix at ${WINEPREFIX} (first boot)"
    wineboot --init >/dev/null 2>&1 || true
    wineserver -w
    # No sound card in a server container: tell Wine to load NO audio driver (the "no sound"
    # setting). Silences the ALSA "cannot find card" spam on every game launch.
    wine reg add 'HKEY_CURRENT_USER\Software\Wine\Drivers' /v Audio /t REG_SZ /d '' /f >/dev/null 2>&1 || true
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

# Stream the BepInEx log to stdout so `docker logs` carries the console + readiness line.
( tail -n +1 -F "${BEPINEX_LOG}" 2>/dev/null ) &
TAIL_PID=$!

# --------------------------------------------------------------------- graceful shutdown
shutdown() {
    log "stop requested — asking the coordinator to save + exit (grace ${STOP_GRACE_SECONDS}s)"
    if [[ "${ENABLE_ADMIN_COMMANDS}" == "1" || "${ENABLE_ADMIN_COMMANDS,,}" == "true" ]]; then
        # Preferred path: the mod polls devcmd.txt at 2 Hz; `quit` runs StopSession (saves the
        # economy stash, tells clients) then exits the process cleanly — no signal into Wine.
        printf 'quit\n' > "${PLUGIN_DIR}/devcmd.txt" 2>/dev/null || true
    else
        # No command file: fall back to signaling Wine and letting Unity's OnApplicationQuit
        # run the mod teardown.
        kill -TERM "${WINE_PID}" 2>/dev/null || true
    fi
    # Wait for the game to exit on its own (the clean path).
    for _ in $(seq 1 "${STOP_GRACE_SECONDS}"); do
        kill -0 "${WINE_PID}" 2>/dev/null || break
        sleep 1
    done
    # Still alive after the grace window: escalate to a signal, then hard-stop Wine.
    if kill -0 "${WINE_PID}" 2>/dev/null; then
        log "grace elapsed — forcing shutdown"
        kill -TERM "${WINE_PID}" 2>/dev/null || true
        sleep 2
    fi
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
