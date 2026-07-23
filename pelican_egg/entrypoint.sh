#!/bin/bash
# Pelican/Pterodactyl container entrypoint. The panel injects every egg variable as an
# environment variable and provides the parsed startup line in ${STARTUP}. We substitute any
# {{VAR}} placeholders, print the resolved command, then exec it so the server process becomes
# PID 1's child (tini) and receives stop signals directly.
set -euo pipefail
cd /home/container

echo "======================================================================"
echo " PUNK Multiverse — dedicated coordinator container"
wine --version 2>/dev/null | sed 's/^/ wine:  /' || echo " wine:  (not found)"
echo " user:  $(id -un)   home: ${HOME}"
echo "======================================================================"

# Default to the server script when launched outside the panel (e.g. `docker run` smoke tests).
STARTUP="${STARTUP:-bash /start-server.sh}"

# {{SERVER_PORT}} -> ${SERVER_PORT}, then let the shell expand against the injected env.
MODIFIED_STARTUP="$(echo "${STARTUP}" | sed -e 's/{{/${/g' -e 's/}}/}/g')"
MODIFIED_STARTUP="$(eval echo "${MODIFIED_STARTUP}")"

echo ":/home/container$ ${MODIFIED_STARTUP}"
# shellcheck disable=SC2086
exec ${MODIFIED_STARTUP}
