using System;
using Steamworks;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Join targets handed to the game on the command line, so something outside the game can start
    /// it straight into a session — PUNK Nexus' Play button, a desktop shortcut, a server's "click
    /// to join" link.
    ///
    /// Two forms, because they come from different places:
    ///
    ///   +connect_lobby &lt;SteamID64&gt;   Steam's own convention. The overlay and the friends list
    ///                                 pass this on a cold start; we did not invent it and must
    ///                                 keep reading it exactly as Steam writes it.
    ///   +punkmv_connect &lt;target&gt;     Ours, and deliberately transport-agnostic: it accepts
    ///                                 anything the JOIN button accepts — host:port, a dedicated
    ///                                 server's SteamID64, or a PMV-XXXXX lobby code. That is what
    ///                                 lets a launcher aim a player at a UDP server without knowing
    ///                                 or changing which transport they have configured.
    ///
    /// Unknown arguments are ignored by Unity, so passing these to a build without the mod is inert.
    /// </summary>
    internal static class LaunchArgs
    {
        public const string LobbyFlag = "+connect_lobby";
        public const string ConnectFlag = "+punkmv_connect";

        /// <summary>Accepted alongside <see cref="ConnectFlag"/> because "+connect host:port" is the
        /// near-universal convention and costs nothing to honor.</summary>
        public const string ConnectAlias = "+connect";

        /// <summary>Steam lobby handed to us on a cold start, or null.</summary>
        public static CSteamID? Lobby()
        {
            var value = ValueOf(LobbyFlag);
            return value != null && ulong.TryParse(value, out var id) && id > 0
                ? new CSteamID(id)
                : (CSteamID?)null;
        }

        /// <summary>
        /// Join target for <see cref="ConnectFlag"/>, or null. Deliberately NOT parsed here — it is
        /// passed to JoinByCode, which already knows how to tell an address from a server id from a
        /// lobby code and picks the transport to match. A second parser would be a second thing to
        /// keep in agreement with the first.
        /// </summary>
        public static string ConnectTarget()
        {
            var value = ValueOf(ConnectFlag) ?? ValueOf(ConnectAlias);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>Describes what was asked for, for the boot log. Null when nothing was.</summary>
        public static string Describe()
        {
            var lobby = Lobby();
            if (lobby.HasValue) return $"{LobbyFlag} {lobby.Value.m_SteamID}";
            var target = ConnectTarget();
            return target != null ? $"{ConnectFlag} {target}" : null;
        }

        private static string ValueOf(string flag)
        {
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return null; }

            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
