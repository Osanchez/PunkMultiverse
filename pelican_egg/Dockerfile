# PUNK Multiverse dedicated coordinator — headless under Wine.
#
# Why a custom image instead of the stock games-steamcmd egg:
#   * PUNK is a Steam *playtest* (appid 2850470). Playtest depots are NOT anonymously
#     downloadable with `steamcmd +login anonymous` — they require an account that has been
#     granted the playtest. So the standard "SteamCMD fetches the server" flow cannot work.
#   * PUNK ships only a Windows Unity build (Punk.exe). There is no Linux dedicated-server
#     binary, so it runs under Wine.
# The game files are therefore supplied by the operator (see README) rather than downloaded.
#
# The coordinator uses the mod's LiteNetLib "Udp" transport, which needs no Steam networking —
# that is what makes a Steam-free headless container possible at all. A steam_appid.txt is
# still written so the base game does not try to relaunch itself through the Steam client.

FROM debian:bookworm-slim

LABEL org.opencontainers.image.title="PUNK Multiverse Server" \
      org.opencontainers.image.description="Headless PUNK Multiverse coordinator (Wine, Udp transport)" \
      org.opencontainers.image.source="https://github.com/Osanchez/PunkMultiverse"

ENV DEBIAN_FRONTEND=noninteractive \
    WINEDEBUG=-all \
    WINEPREFIX=/home/container/.wine \
    DISPLAY=:0 \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8

# 32-bit multiarch + WineHQ stable (the game is a 64-bit build, but Wine still pulls 32-bit
# support libraries). Xvfb gives Unity a display to initialize against even in -nographics;
# tini reaps zombies and forwards stop signals to the server script.
RUN dpkg --add-architecture i386 \
 && apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates wget gnupg2 \
 && mkdir -pm755 /etc/apt/keyrings \
 && wget -q -O /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key \
 && wget -qNP /etc/apt/sources.list.d/ https://dl.winehq.org/wine-builds/debian/dists/bookworm/winehq-bookworm.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      winehq-stable \
      xvfb xauth \
      cabextract procps psmisc \
      curl tar unzip xz-utils \
      tini locales netcat-openbsd \
 && sed -i 's/^# *\(C.UTF-8\)/\1/' /etc/locale.gen 2>/dev/null || true \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

COPY entrypoint.sh /entrypoint.sh
COPY start-server.sh /start-server.sh
RUN chmod 0755 /entrypoint.sh /start-server.sh

# Pelican runs game servers as an unprivileged user whose home is /home/container.
RUN useradd -m -d /home/container -s /bin/bash container
USER container
ENV USER=container HOME=/home/container
WORKDIR /home/container

# tini -g forwards signals to the whole process group so a panel "Stop" reaches start-server.sh.
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/entrypoint.sh"]
