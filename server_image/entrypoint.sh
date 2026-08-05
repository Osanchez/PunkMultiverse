#!/bin/bash
# Container entrypoint. Prints the environment banner, then EXECs the server script so the game
# process becomes PID 1's (tini's) child and receives stop signals directly — `docker stop` has to
# reach the mod for its save-and-notify shutdown to run, which it cannot do through a wrapper.
set -euo pipefail
cd /home/container

echo "======================================================================"
echo " PUNK Multiverse — dedicated coordinator container"
wine --version 2>/dev/null | sed 's/^/ wine:  /' || echo " wine:  (not found)"
echo " user:  $(id -un)   home: ${HOME}"
echo "======================================================================"

# The server script unless something overrides it — a smoke test can point STARTUP at a shell.
STARTUP="${STARTUP:-bash /start-server.sh}"

# {{SERVER_PORT}} -> ${SERVER_PORT}, then let the shell expand against the container env.
MODIFIED_STARTUP="$(echo "${STARTUP}" | sed -e 's/{{/${/g' -e 's/}}/}/g')"
MODIFIED_STARTUP="$(eval echo "${MODIFIED_STARTUP}")"

echo ":/home/container$ ${MODIFIED_STARTUP}"
# shellcheck disable=SC2086
exec ${MODIFIED_STARTUP}
