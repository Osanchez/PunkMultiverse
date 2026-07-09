using System;
using System.Text;
using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Steam lobby lifecycle: create (friends-only, 4 slots), join by id, overlay invites,
    /// "+connect_lobby" launch args, and the human-pasteable lobby code. The lobby is only the
    /// meet-up point — all gameplay traffic runs over SteamMessagesTransport P2P; the roster
    /// lives in NetSession so loopback and Steam share one code path.
    /// Callbacks dispatch on the game's own SteamAPI.RunCallbacks pump.
    /// </summary>
    public sealed class SteamLobbyController
    {
        public const string KeyModVersion = "pmvver";
        public const string KeyGameVersion = "gamebuild";
        public const string KeyHostId = "host";

        private CallResult<LobbyCreated_t> _lobbyCreated;
        private CallResult<LobbyEnter_t> _lobbyEntered;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyChatUpdate_t> _chatUpdate;

        public CSteamID CurrentLobby { get; private set; }
        public bool InLobby => CurrentLobby.IsValid() && CurrentLobby.IsLobby();

        /// <summary>Fired when the lobby is created and its metadata is set (host flow).</summary>
        public event Action<CSteamID> LobbyCreated;
        /// <summary>Fired after successfully entering a lobby, with the host's SteamID64 (client flow).</summary>
        public event Action<CSteamID, ulong> LobbyJoined;
        /// <summary>Fired on any failure with a user-facing message.</summary>
        public event Action<string> LobbyError;
        /// <summary>Fired when a Steam overlay invite / "join game" asks us to join.</summary>
        public event Action<CSteamID> JoinRequested;

        public SteamLobbyController()
        {
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => JoinRequested?.Invoke(r.m_steamIDLobby));
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        }

        public void Dispose()
        {
            LeaveLobby();
            _lobbyCreated?.Dispose();
            _lobbyEntered?.Dispose();
            _joinRequested?.Dispose();
            _chatUpdate?.Dispose();
        }

        // ---------------------------------------------------------------- host

        public void CreateLobby()
        {
            var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Core.NetSession.MaxPlayers);
            _lobbyCreated.Set(call);
            Plugin.Log.LogInfo("[Lobby] creating Steam lobby…");
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                LobbyError?.Invoke($"Could not create Steam lobby ({(ioFailure ? "IO failure" : result.m_eResult.ToString())}).");
                return;
            }
            CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(CurrentLobby, KeyModVersion, Plugin.Version);
            SteamMatchmaking.SetLobbyData(CurrentLobby, KeyGameVersion, UnityEngine.Application.version);
            SteamMatchmaking.SetLobbyData(CurrentLobby, KeyHostId, SteamUser.GetSteamID().m_SteamID.ToString());
            Plugin.Log.LogInfo($"[Lobby] created {CurrentLobby.m_SteamID}, code {EncodeLobbyCode(CurrentLobby)}");
            LobbyCreated?.Invoke(CurrentLobby);
        }

        // ---------------------------------------------------------------- client

        public void JoinLobby(CSteamID lobbyId)
        {
            var call = SteamMatchmaking.JoinLobby(lobbyId);
            _lobbyEntered.Set(call);
            Plugin.Log.LogInfo($"[Lobby] joining {lobbyId.m_SteamID}…");
        }

        private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
        {
            if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                LobbyError?.Invoke($"Could not join lobby ({(ioFailure ? "IO failure" : ((EChatRoomEnterResponse)result.m_EChatRoomEnterResponse).ToString())}).");
                return;
            }
            CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);

            // Friendly pre-check; the HELLO handshake stays authoritative.
            string hostVer = SteamMatchmaking.GetLobbyData(CurrentLobby, KeyModVersion);
            if (!string.IsNullOrEmpty(hostVer) && hostVer != Plugin.Version)
            {
                LeaveLobby();
                LobbyError?.Invoke($"Version mismatch: host runs mod v{hostVer}, you have v{Plugin.Version}.");
                return;
            }

            string hostIdStr = SteamMatchmaking.GetLobbyData(CurrentLobby, KeyHostId);
            if (!ulong.TryParse(hostIdStr, out var hostId) || hostId == 0)
            {
                LeaveLobby();
                LobbyError?.Invoke("Lobby has no host data (host offline?).");
                return;
            }
            Plugin.Log.LogInfo($"[Lobby] entered {CurrentLobby.m_SteamID}, host {hostId}");
            LobbyJoined?.Invoke(CurrentLobby, hostId);
        }

        // ---------------------------------------------------------------- shared

        public void LeaveLobby()
        {
            if (!InLobby) return;
            SteamMatchmaking.LeaveLobby(CurrentLobby);
            Plugin.Log.LogInfo($"[Lobby] left {CurrentLobby.m_SteamID}");
            CurrentLobby = default;
        }

        public void OpenInviteOverlay()
        {
            if (InLobby) SteamFriends.ActivateGameOverlayInviteDialog(CurrentLobby);
        }

        /// <summary>Transport session gate: only current lobby members get P2P sessions accepted.</summary>
        public bool IsMember(CSteamID user)
        {
            if (!InLobby) return false;
            int n = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
            for (int i = 0; i < n; i++)
                if (SteamMatchmaking.GetLobbyMemberByIndex(CurrentLobby, i) == user)
                    return true;
            return false;
        }

        private void OnChatUpdate(LobbyChatUpdate_t update)
        {
            // Membership changes are interesting for the session gate only; roster truth is P2P.
            var change = (EChatMemberStateChange)update.m_rgfChatMemberStateChange;
            Plugin.Log.LogDebug($"[Lobby] member {update.m_ulSteamIDUserChanged}: {change}");
        }

        /// <summary>Scan launch args for "+connect_lobby &lt;id&gt;" (Steam adds this on cold-start joins).</summary>
        public static CSteamID? ParseLaunchArgs()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("+connect_lobby", StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(args[i + 1], out var id) && id > 0)
                    return new CSteamID(id);
            return null;
        }

        // ---------------------------------------------------------------- lobby code codec

        // Crockford base-32: no I/L/O/U, case-insensitive. 64-bit id -> 13 chars + 1 checksum,
        // rendered as PMV-XXXXX-XXXXX-XXXX.
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        public static string EncodeLobbyCode(CSteamID lobbyId)
        {
            ulong v = lobbyId.m_SteamID;
            var chars = new char[13];
            for (int i = 12; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(v & 31)];
                v >>= 5;
            }
            int checksum = 0;
            foreach (var c in chars) checksum = (checksum + Alphabet.IndexOf(c)) % 32;
            var raw = new string(chars) + Alphabet[checksum];
            var sb = new StringBuilder("PMV-");
            sb.Append(raw, 0, 5).Append('-').Append(raw, 5, 5).Append('-').Append(raw, 10, 4);
            return sb.ToString();
        }

        public static bool TryDecodeLobbyCode(string code, out CSteamID lobbyId)
        {
            lobbyId = default;
            if (string.IsNullOrWhiteSpace(code)) return false;
            var sb = new StringBuilder();
            foreach (var raw in code.ToUpperInvariant())
            {
                var c = raw switch { 'I' => '1', 'L' => '1', 'O' => '0', _ => raw };
                if (Alphabet.IndexOf(c) >= 0) sb.Append(c);
            }
            var s = sb.ToString();
            // Strip the "PMV" prefix only when the full prefixed length matches.
            if (s.Length == 17 && s.StartsWith("PMV")) s = s.Substring(3);
            if (s.Length != 14) return false;

            int checksum = 0;
            ulong v = 0;
            for (int i = 0; i < 13; i++)
            {
                int digit = Alphabet.IndexOf(s[i]);
                v = (v << 5) | (uint)digit;
                checksum = (checksum + digit) % 32;
            }
            if (Alphabet[checksum] != s[13]) return false;
            lobbyId = new CSteamID(v);
            return lobbyId.IsValid() && lobbyId.IsLobby();
        }
    }
}
