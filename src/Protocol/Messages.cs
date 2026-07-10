using System.Collections.Generic;

namespace PunkMultiverse.Protocol
{
    // One struct per wire message. Each writes its MsgType header itself so callers can't
    // mismatch header and body. Extended milestone by milestone.

    public struct HelloMsg
    {
        public int ProtocolVersion;
        public string ModVersion;
        public string GameVersion;
        public ulong SteamId;      // 0 on loopback
        public string Name;
        public bool Resuming;      // host-migration reattach: already in-world, skip the regen
        public string Mods;        // canonical BepInEx plugin manifest (ModManifest.Local)

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.Hello);
            w.WriteVarUInt((uint)ProtocolVersion);
            w.WriteString(ModVersion);
            w.WriteString(GameVersion);
            w.WriteULong(SteamId);
            w.WriteString(Name);
            w.WriteBool(Resuming);
            w.WriteString(Mods);
        }

        public static HelloMsg Read(NetReader r) => new HelloMsg
        {
            ProtocolVersion = (int)r.ReadVarUInt(),
            ModVersion = r.ReadString(),
            GameVersion = r.ReadString(),
            SteamId = r.ReadULong(),
            Name = r.ReadString(),
            Resuming = r.ReadBool(),
            Mods = r.ReadString(),
        };
    }

    public struct RosterEntry
    {
        public byte Slot;
        public ulong PeerId;
        public string Name;
        public byte ColorIndex;
        public bool Ready;
        public bool Connected;
        public bool ModsMismatch; // joiner's plugin set differs from the host's (Warn policy)

        public void Write(NetWriter w)
        {
            w.WriteByte(Slot);
            w.WriteULong(PeerId);
            w.WriteString(Name);
            w.WriteByte(ColorIndex);
            w.WriteBool(Ready);
            w.WriteBool(Connected);
            w.WriteBool(ModsMismatch);
        }

        public static RosterEntry Read(NetReader r) => new RosterEntry
        {
            Slot = r.ReadByte(),
            PeerId = r.ReadULong(),
            Name = r.ReadString(),
            ColorIndex = r.ReadByte(),
            Ready = r.ReadBool(),
            Connected = r.ReadBool(),
            ModsMismatch = r.ReadBool(),
        };
    }

    public struct WelcomeMsg
    {
        public byte Slot;
        public string HostModVersion;
        public List<RosterEntry> Roster;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.Welcome);
            w.WriteByte(Slot);
            w.WriteString(HostModVersion);
            w.WriteByte((byte)(Roster?.Count ?? 0));
            if (Roster != null) foreach (var e in Roster) e.Write(w);
        }

        public static WelcomeMsg Read(NetReader r)
        {
            var m = new WelcomeMsg { Slot = r.ReadByte(), HostModVersion = r.ReadString(), Roster = new List<RosterEntry>() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++) m.Roster.Add(RosterEntry.Read(r));
            return m;
        }
    }

    public struct RejectMsg
    {
        public string Reason;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.Reject);
            w.WriteString(Reason);
        }

        public static RejectMsg Read(NetReader r) => new RejectMsg { Reason = r.ReadString() };
    }

    public struct LobbyStateMsg
    {
        public List<RosterEntry> Roster;
        public int HostSeed; // 0 = random at start

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.LobbyState);
            w.WriteInt(HostSeed);
            w.WriteByte((byte)(Roster?.Count ?? 0));
            if (Roster != null) foreach (var e in Roster) e.Write(w);
        }

        public static LobbyStateMsg Read(NetReader r)
        {
            var m = new LobbyStateMsg { HostSeed = r.ReadInt(), Roster = new List<RosterEntry>() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++) m.Roster.Add(RosterEntry.Read(r));
            return m;
        }
    }

    public struct PlayerLeftMsg
    {
        public byte Slot;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.PlayerLeft);
            w.WriteByte(Slot);
        }

        public static PlayerLeftMsg Read(NetReader r) => new PlayerLeftMsg { Slot = r.ReadByte() };
    }

    public struct SetLobbyPrefsMsg
    {
        public byte ColorIndex;
        public bool Ready;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.SetLobbyPrefs);
            w.WriteByte(ColorIndex);
            w.WriteBool(Ready);
        }

        public static SetLobbyPrefsMsg Read(NetReader r) => new SetLobbyPrefsMsg
        {
            ColorIndex = r.ReadByte(),
            Ready = r.ReadBool(),
        };
    }

    public struct StartRunMsg
    {
        public int Seed;
        public bool IsRejoin;          // reconnecting into a run in progress
        public int SpawnStationNetId;  // rejoin/late-join spawn checkpoint; 0 = run start

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.StartRun);
            w.WriteInt(Seed);
            w.WriteBool(IsRejoin);
            w.WriteVarUInt((uint)SpawnStationNetId);
        }

        public static StartRunMsg Read(NetReader r) => new StartRunMsg
        {
            Seed = r.ReadInt(),
            IsRejoin = r.ReadBool(),
            SpawnStationNetId = (int)r.ReadVarUInt(),
        };
    }

    public struct LevelReadyMsg
    {
        public ulong Checksum;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.LevelReady);
            w.WriteULong(Checksum);
        }

        public static LevelReadyMsg Read(NetReader r) => new LevelReadyMsg { Checksum = r.ReadULong() };
    }

    [System.Flags]
    public enum ShipFlags : byte
    {
        None = 0,
        Dead = 1,
        Boost = 2,
        Hover = 4,
    }

    public struct ShipStateMsg
    {
        public byte Slot;
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Vel;
        public float RotDeg;
        public UnityEngine.Vector2 Aim;
        public UnityEngine.Vector2 Move; // owner's flyDirection input — drives puppet engine VFX
        public ShipFlags Flags;
        public float HpFraction;
        public float ShieldFraction;
        public float BurnLevel;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.ShipState);
            w.WriteByte(Slot);
            w.WritePosition(Pos);
            w.WriteVector2Half(Vel);
            w.WriteHalf(RotDeg);
            w.WriteVector2Half(Aim);
            w.WriteVector2Half(Move);
            w.WriteByte((byte)Flags);
            w.WriteHalf(HpFraction);
            w.WriteHalf(ShieldFraction);
            w.WriteHalf(BurnLevel);
        }

        public static ShipStateMsg Read(NetReader r) => new ShipStateMsg
        {
            Slot = r.ReadByte(),
            Pos = r.ReadPosition(),
            Vel = r.ReadVector2Half(),
            RotDeg = r.ReadHalf(),
            Aim = r.ReadVector2Half(),
            Move = r.ReadVector2Half(),
            Flags = (ShipFlags)r.ReadByte(),
            HpFraction = r.ReadHalf(),
            ShieldFraction = r.ReadHalf(),
            BurnLevel = r.ReadHalf(),
        };
    }

    public struct ShipDashMsg
    {
        public byte Slot;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.ShipDash);
            w.WriteByte(Slot);
        }

        public static ShipDashMsg Read(NetReader r) => new ShipDashMsg { Slot = r.ReadByte() };
    }

    public struct ManifestMsg
    {
        public ushort StartIndex;
        public ushort Total;
        public ulong[] Fps;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.Manifest);
            w.WriteUShort(StartIndex);
            w.WriteUShort(Total);
            w.WriteByte((byte)Fps.Length);
            foreach (var fp in Fps) w.WriteULong(fp);
        }

        public static ManifestMsg Read(NetReader r)
        {
            var m = new ManifestMsg { StartIndex = r.ReadUShort(), Total = r.ReadUShort() };
            int n = r.ReadByte();
            m.Fps = new ulong[n];
            for (int i = 0; i < n; i++) m.Fps[i] = r.ReadULong();
            return m;
        }
    }

    public struct FireEventMsg
    {
        public byte Slot;
        public byte Holder; // 0 = primary, 1 = secondary
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Dir;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.FireEvent);
            w.WriteByte(Slot);
            w.WriteByte(Holder);
            w.WritePosition(Pos);
            w.WriteVector2Half(Dir);
        }

        public static FireEventMsg Read(NetReader r) => new FireEventMsg
        {
            Slot = r.ReadByte(),
            Holder = r.ReadByte(),
            Pos = r.ReadPosition(),
            Dir = r.ReadVector2Half(),
        };
    }

    public struct DamageRequestMsg
    {
        public bool IsEntity;   // false: TargetSlot is a player; true: TargetNetId is an entity
        public byte TargetSlot;
        public int TargetNetId;
        public float Amount;
        public uint TypeHash; // FNV-1a of Resource.name; 0 = untyped

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.DamageRequest);
            w.WriteBool(IsEntity);
            w.WriteByte(TargetSlot);
            w.WriteVarUInt((uint)TargetNetId);
            w.WriteFloat(Amount);
            w.WriteUInt(TypeHash);
        }

        public static DamageRequestMsg Read(NetReader r) => new DamageRequestMsg
        {
            IsEntity = r.ReadBool(),
            TargetSlot = r.ReadByte(),
            TargetNetId = (int)r.ReadVarUInt(),
            Amount = r.ReadFloat(),
            TypeHash = r.ReadUInt(),
        };
    }

    public struct AuthAssignMsg
    {
        public System.Collections.Generic.List<(int netId, byte owner)> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.AuthAssign);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var (netId, owner) in Entries)
            {
                w.WriteVarUInt((uint)netId);
                w.WriteByte(owner);
            }
        }

        public static AuthAssignMsg Read(NetReader r)
        {
            int n = (int)r.ReadVarUInt();
            var m = new AuthAssignMsg { Entries = new System.Collections.Generic.List<(int, byte)>(n) };
            for (int i = 0; i < n; i++) m.Entries.Add(((int)r.ReadVarUInt(), r.ReadByte()));
            return m;
        }
    }

    public struct EntityStateEntry
    {
        public int NetId;
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Vel;
        public float Rot;
        public UnityEngine.Vector2 Aim; // authority's visual facing/aim; zero = unknown
        public byte State;              // index into the unit's StateMachine states; 255 = none
        public byte Fire;               // weapon audio state: 0 idle, 1 warming up, 2 firing loop
        public byte Ammo;               // weapon resource tank fraction, 0..254; 255 = none/shared
        public float HpFraction;
        public float ShieldFraction;
        public float BurnLevel;
    }

    public struct EntityStateMsg
    {
        public System.Collections.Generic.List<EntityStateEntry> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityState);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var e in Entries)
            {
                w.WriteVarUInt((uint)e.NetId);
                w.WritePosition(e.Pos);
                w.WriteVector2Half(e.Vel);
                w.WriteHalf(e.Rot);
                w.WriteVector2Half(e.Aim);
                w.WriteByte(e.State);
                w.WriteByte(e.Fire);
                w.WriteByte(e.Ammo);
                w.WriteHalf(e.HpFraction);
                w.WriteHalf(e.ShieldFraction);
                w.WriteHalf(e.BurnLevel);
            }
        }

        public static EntityStateMsg Read(NetReader r)
        {
            int n = (int)r.ReadVarUInt();
            var m = new EntityStateMsg { Entries = new System.Collections.Generic.List<EntityStateEntry>(n) };
            for (int i = 0; i < n; i++)
            {
                m.Entries.Add(new EntityStateEntry
                {
                    NetId = (int)r.ReadVarUInt(),
                    Pos = r.ReadPosition(),
                    Vel = r.ReadVector2Half(),
                    Rot = r.ReadHalf(),
                    Aim = r.ReadVector2Half(),
                    State = r.ReadByte(),
                    Fire = r.ReadByte(),
                    Ammo = r.ReadByte(),
                    HpFraction = r.ReadHalf(),
                    ShieldFraction = r.ReadHalf(),
                    BurnLevel = r.ReadHalf(),
                });
            }
            return m;
        }
    }

    public struct EntityKilledMsg
    {
        public int NetId;
        public byte KillerSlot;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityKilled);
            w.WriteVarUInt((uint)NetId);
            w.WriteByte(KillerSlot);
        }

        public static EntityKilledMsg Read(NetReader r) => new EntityKilledMsg
        {
            NetId = (int)r.ReadVarUInt(),
            KillerSlot = r.ReadByte(),
        };
    }

    public struct ShipLifeMsg // ShipDied / ShipResurrected share the body
    {
        public byte Slot;

        public void Write(NetWriter w, bool died)
        {
            w.WriteMsgType(died ? MsgType.ShipDied : MsgType.ShipResurrected);
            w.WriteByte(Slot);
        }

        public static ShipLifeMsg Read(NetReader r) => new ShipLifeMsg { Slot = r.ReadByte() };
    }

    public struct CellDiffMsg
    {
        public System.Collections.Generic.List<(int index, byte type)> Cells;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.CellDiff);
            w.WriteVarUInt((uint)Cells.Count);
            int prev = 0;
            foreach (var (index, type) in Cells)
            {
                w.WriteVarUInt((uint)(index - prev)); // indexes are sorted ascending
                w.WriteByte(type);
                prev = index;
            }
        }

        public static CellDiffMsg Read(NetReader r)
        {
            int count = (int)r.ReadVarUInt();
            var cells = new System.Collections.Generic.List<(int, byte)>(count);
            int prev = 0;
            for (int i = 0; i < count; i++)
            {
                prev += (int)r.ReadVarUInt();
                cells.Add((prev, r.ReadByte()));
            }
            return new CellDiffMsg { Cells = cells };
        }
    }

    public struct EntityFireMsg
    {
        public int NetId;
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Dir;
        public UnityEngine.Vector2 BodyPos; // shooter's body position at fire time — lets the
                                            // replay re-anchor the muzzle to the local puppet
        public byte TargetSlot;             // player the shooter is targeting (homing), 255 = none

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityFire);
            w.WriteVarUInt((uint)NetId);
            w.WritePosition(Pos);
            w.WriteVector2Half(Dir);
            w.WritePosition(BodyPos);
            w.WriteByte(TargetSlot);
        }

        public static EntityFireMsg Read(NetReader r) => new EntityFireMsg
        {
            NetId = (int)r.ReadVarUInt(),
            Pos = r.ReadPosition(),
            Dir = r.ReadVector2Half(),
            BodyPos = r.ReadPosition(),
            TargetSlot = r.ReadByte(),
        };
    }

    public struct InstrumentUsedMsg
    {
        public int NetId;
        public uint DiscoverableHash; // FNV-1a of the InstrumentDiscoverable asset name

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.InstrumentUsed);
            w.WriteVarUInt((uint)NetId);
            w.WriteUInt(DiscoverableHash);
        }

        public static InstrumentUsedMsg Read(NetReader r) => new InstrumentUsedMsg
        {
            NetId = (int)r.ReadVarUInt(),
            DiscoverableHash = r.ReadUInt(),
        };
    }

    public struct EntitySpawnedMsg
    {
        public int NetId;
        public byte OwnerSlot;
        public string EntityId;
        public UnityEngine.Vector2 Pos;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntitySpawned);
            w.WriteVarUInt((uint)NetId);
            w.WriteByte(OwnerSlot);
            w.WriteString(EntityId);
            w.WritePosition(Pos);
        }

        public static EntitySpawnedMsg Read(NetReader r) => new EntitySpawnedMsg
        {
            NetId = (int)r.ReadVarUInt(),
            OwnerSlot = r.ReadByte(),
            EntityId = r.ReadString(),
            Pos = r.ReadPosition(),
        };
    }

    public struct HookStateMsg
    {
        public byte Slot;
        public bool Attached;
        public int TargetNetId;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.HookState);
            w.WriteByte(Slot);
            w.WriteBool(Attached);
            w.WriteVarUInt((uint)TargetNetId);
        }

        public static HookStateMsg Read(NetReader r) => new HookStateMsg
        {
            Slot = r.ReadByte(),
            Attached = r.ReadBool(),
            TargetNetId = (int)r.ReadVarUInt(),
        };
    }

    public struct MinionSpawnedMsg
    {
        public int NetId;       // runtime id: (ownerSlot+1)<<12 | counter
        public byte OwnerSlot;
        public string EntityId; // SavablesCollection prefab key
        public UnityEngine.Vector2 Pos;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.MinionSpawned);
            w.WriteVarUInt((uint)NetId);
            w.WriteByte(OwnerSlot);
            w.WriteString(EntityId);
            w.WritePosition(Pos);
        }

        public static MinionSpawnedMsg Read(NetReader r) => new MinionSpawnedMsg
        {
            NetId = (int)r.ReadVarUInt(),
            OwnerSlot = r.ReadByte(),
            EntityId = r.ReadString(),
            Pos = r.ReadPosition(),
        };
    }

    public struct StationUpgradeMsg
    {
        public int StationNetId;
        public uint UpgradeHash; // FNV-1a of the StationUpgrade asset name

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.StationUpgrade);
            w.WriteVarUInt((uint)StationNetId);
            w.WriteUInt(UpgradeHash);
        }

        public static StationUpgradeMsg Read(NetReader r) => new StationUpgradeMsg
        {
            StationNetId = (int)r.ReadVarUInt(),
            UpgradeHash = r.ReadUInt(),
        };
    }

    public struct ScannerUsedMsg
    {
        public int NetId;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.ScannerUsed);
            w.WriteVarUInt((uint)NetId);
        }

        public static ScannerUsedMsg Read(NetReader r) => new ScannerUsedMsg { NetId = (int)r.ReadVarUInt() };
    }

    public struct PingMsg
    {
        public uint TimeMs;

        public void Write(NetWriter w, bool pong)
        {
            w.WriteMsgType(pong ? MsgType.Pong : MsgType.Ping);
            w.WriteUInt(TimeMs);
        }

        public static PingMsg Read(NetReader r) => new PingMsg { TimeMs = r.ReadUInt() };
    }
}
