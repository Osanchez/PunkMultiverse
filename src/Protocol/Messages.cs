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
        public ulong SteamId;      // stable identity: SteamID64 or loopback install identity
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
        public ulong IdentityId;
        public string Name;
        public byte ColorIndex;
        public bool Ready;
        public bool Connected;
        public bool NeedsStationRespawn;
        public int RespawnStationNetId;
        public bool ModsMismatch; // joiner's plugin set differs from the host's (Warn policy)

        public void Write(NetWriter w)
        {
            w.WriteByte(Slot);
            w.WriteULong(PeerId);
            w.WriteULong(IdentityId);
            w.WriteString(Name);
            w.WriteByte(ColorIndex);
            w.WriteBool(Ready);
            w.WriteBool(Connected);
            w.WriteBool(NeedsStationRespawn);
            w.WriteVarUInt((uint)RespawnStationNetId);
            w.WriteBool(ModsMismatch);
        }

        public static RosterEntry Read(NetReader r) => new RosterEntry
        {
            Slot = r.ReadByte(),
            PeerId = r.ReadULong(),
            IdentityId = r.ReadULong(),
            Name = r.ReadString(),
            ColorIndex = r.ReadByte(),
            Ready = r.ReadBool(),
            Connected = r.ReadBool(),
            NeedsStationRespawn = r.ReadBool(),
            RespawnStationNetId = (int)r.ReadVarUInt(),
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
        public int HostSeed;       // 0 = random at start
        public bool FriendlyFire;  // host's game-settings choice; every client enforces it

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.LobbyState);
            w.WriteInt(HostSeed);
            w.WriteBool(FriendlyFire);
            w.WriteByte((byte)(Roster?.Count ?? 0));
            if (Roster != null) foreach (var e in Roster) e.Write(w);
        }

        public static LobbyStateMsg Read(NetReader r)
        {
            var m = new LobbyStateMsg
            {
                HostSeed = r.ReadInt(),
                FriendlyFire = r.ReadBool(),
                Roster = new List<RosterEntry>(),
            };
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
        public bool IsResume;          // whole-party resume of a saved run (terrain from local save)
        public int SpawnStationNetId;  // rejoin/late-join spawn checkpoint; 0 = run start
        public float EnemyHpMult;      // enemy max-health multiplier for this run; <=0 = 1x

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.StartRun);
            w.WriteInt(Seed);
            w.WriteBool(IsRejoin);
            w.WriteBool(IsResume);
            w.WriteVarUInt((uint)SpawnStationNetId);
            w.WriteHalf(EnemyHpMult);
        }

        public static StartRunMsg Read(NetReader r) => new StartRunMsg
        {
            Seed = r.ReadInt(),
            IsRejoin = r.ReadBool(),
            IsResume = r.ReadBool(),
            SpawnStationNetId = (int)r.ReadVarUInt(),
            EnemyHpMult = r.ReadHalf(),
        };
    }

    public struct LevelReadyMsg
    {
        public ulong Checksum;
        public int EntityCount;
        public ulong EntityDigest;
        public int PlantCount;
        public ulong PlantDigest;
        public int VisualVariantCount;
        public ulong VisualVariantDigest;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.LevelReady);
            w.WriteULong(Checksum);
            w.WriteVarUInt((uint)EntityCount);
            w.WriteULong(EntityDigest);
            w.WriteVarUInt((uint)PlantCount);
            w.WriteULong(PlantDigest);
            w.WriteVarUInt((uint)VisualVariantCount);
            w.WriteULong(VisualVariantDigest);
        }

        public static LevelReadyMsg Read(NetReader r) => new LevelReadyMsg
        {
            Checksum = r.ReadULong(),
            EntityCount = (int)r.ReadVarUInt(),
            EntityDigest = r.ReadULong(),
            PlantCount = (int)r.ReadVarUInt(),
            PlantDigest = r.ReadULong(),
            VisualVariantCount = (int)r.ReadVarUInt(),
            VisualVariantDigest = r.ReadULong(),
        };
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
        public byte ViewSlot; // ship/camera whose world interest this sender is currently viewing
        public bool HasBody; // false after death: view routing only, no ship pose follows
        public uint TimeMs; // sender's unscaled clock — ordering + jitter-free interpolation
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
            w.WriteByte(ViewSlot);
            w.WriteBool(HasBody);
            w.WriteUInt(TimeMs);
            if (!HasBody) return;
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

        public static ShipStateMsg Read(NetReader r)
        {
            var msg = new ShipStateMsg
            {
                Slot = r.ReadByte(),
                ViewSlot = r.ReadByte(),
                HasBody = r.ReadBool(),
                TimeMs = r.ReadUInt(),
            };
            if (!msg.HasBody) return msg;
            msg.Pos = r.ReadPosition();
            msg.Vel = r.ReadVector2Half();
            msg.RotDeg = r.ReadHalf();
            msg.Aim = r.ReadVector2Half();
            msg.Move = r.ReadVector2Half();
            msg.Flags = (ShipFlags)r.ReadByte();
            msg.HpFraction = r.ReadHalf();
            msg.ShieldFraction = r.ReadHalf();
            msg.BurnLevel = r.ReadHalf();
            return msg;
        }
    }

    public struct AuthReleaseMsg
    {
        public int NetId;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.AuthRelease);
            w.WriteVarUInt((uint)NetId);
        }

        public static AuthReleaseMsg Read(NetReader r) => new AuthReleaseMsg { NetId = (int)r.ReadVarUInt() };
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
        public uint TimeMs; // shooter's unscaled clock; replay follows the puppet timeline
        public UnityEngine.Vector2 BodyPos;
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Dir;
        public int Seed;    // RNG seed the shooter used for this burst tick — replays match exactly
        public uint ShotId; // origin-slot-prefixed, monotonic burst identity

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.FireEvent);
            w.WriteByte(Slot);
            w.WriteByte(Holder);
            w.WriteUInt(TimeMs);
            w.WritePosition(BodyPos);
            w.WritePosition(Pos);
            w.WriteVector2Half(Dir);
            w.WriteInt(Seed);
            w.WriteUInt(ShotId);
        }

        public static FireEventMsg Read(NetReader r) => new FireEventMsg
        {
            Slot = r.ReadByte(),
            Holder = r.ReadByte(),
            TimeMs = r.ReadUInt(),
            BodyPos = r.ReadPosition(),
            Pos = r.ReadPosition(),
            Dir = r.ReadVector2Half(),
            Seed = r.ReadInt(),
            ShotId = r.ReadUInt(),
        };
    }

    public struct PlantFruitKilledMsg
    {
        public int PlantNetId;
        public int FruitId;
        public byte KillerSlot;
        public uint Lifetime;
        public uint MutationRevision;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.PlantFruitKilled);
            w.WriteVarUInt((uint)PlantNetId);
            w.WriteInt(FruitId);
            w.WriteByte(KillerSlot);
            w.WriteVarUInt(Lifetime);
            w.WriteVarUInt(MutationRevision);
        }

        public static PlantFruitKilledMsg Read(NetReader r) => new PlantFruitKilledMsg
        {
            PlantNetId = (int)r.ReadVarUInt(),
            FruitId = r.ReadInt(),
            KillerSlot = r.ReadByte(),
            Lifetime = r.ReadVarUInt(),
            MutationRevision = r.ReadVarUInt(),
        };
    }

    public struct DamageRequestMsg
    {
        public bool IsEntity;   // false: TargetSlot is a player; true: TargetNetId is an entity
        public byte TargetSlot;
        public int TargetNetId;
        public uint TargetLifetime;
        public float Amount;
        public uint TypeHash; // FNV-1a of Resource.name; 0 = untyped
        public byte AttackerSlot; // who dealt this damage — travels with it so the owner credits the real killer
        public uint RequestId; // unique per attacker; protects reliable routing/reconnect replay
        public uint ShotId;
        public ushort ProjectileOrdinal;
        // A dormant-claim replay to a NEW owner. Every peer already recorded RequestId as seen
        // when the original broadcast passed through, so replays must bypass the dedup or the
        // claim is dead on arrival at any remote owner.
        public bool Replay;

        public void Write(NetWriter w) => Write(w, MsgType.DamageRequest);

        /// <summary>Same payload rides two message types: the ordinary request and the owner's
        /// DamageUnservable bounce to the host (which must carry the full original claim).</summary>
        public void Write(NetWriter w, MsgType type)
        {
            w.WriteMsgType(type);
            w.WriteBool(IsEntity);
            w.WriteByte(TargetSlot);
            w.WriteVarUInt((uint)TargetNetId);
            w.WriteVarUInt(TargetLifetime);
            w.WriteFloat(Amount);
            w.WriteUInt(TypeHash);
            w.WriteByte(AttackerSlot);
            w.WriteUInt(RequestId);
            w.WriteUInt(ShotId);
            w.WriteUShort(ProjectileOrdinal);
            w.WriteBool(Replay);
        }

        public static DamageRequestMsg Read(NetReader r) => new DamageRequestMsg
        {
            IsEntity = r.ReadBool(),
            TargetSlot = r.ReadByte(),
            TargetNetId = (int)r.ReadVarUInt(),
            TargetLifetime = r.ReadVarUInt(),
            Amount = r.ReadFloat(),
            TypeHash = r.ReadUInt(),
            AttackerSlot = r.ReadByte(),
            RequestId = r.ReadUInt(),
            ShotId = r.ReadUInt(),
            ProjectileOrdinal = r.ReadUShort(),
            Replay = r.ReadBool(),
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

    /// <summary>A peer proves availability by reporting a concrete puppet it currently has
    /// instantiated but for which no authoritative snapshots are arriving.</summary>
    public struct EntityStarvedRequestMsg
    {
        public int NetId;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityStarvedRequest);
            w.WriteVarUInt((uint)NetId);
        }

        public static EntityStarvedRequestMsg Read(NetReader r) => new EntityStarvedRequestMsg
        {
            NetId = (int)r.ReadVarUInt(),
        };
    }

    public struct EntityAuthorityPrepareMsg
    {
        public int NetId;
        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityAuthorityPrepare);
            w.WriteVarUInt((uint)NetId);
        }
        public static EntityAuthorityPrepareMsg Read(NetReader r) => new EntityAuthorityPrepareMsg
        {
            NetId = (int)r.ReadVarUInt(),
        };
    }

    public struct EntityAuthorityAckMsg
    {
        public int NetId;
        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityAuthorityAck);
            w.WriteVarUInt((uint)NetId);
        }
        public static EntityAuthorityAckMsg Read(NetReader r) => new EntityAuthorityAckMsg
        {
            NetId = (int)r.ReadVarUInt(),
        };
    }

    public struct EntityStateEntry
    {
        [System.Flags]
        public enum Fields : byte
        {
            None = 0,
            Aim = 1,
            Status = 2,
            Vitals = 4,
            Full = Aim | Status | Vitals,
        }

        public int NetId;
        public uint Lifetime;
        public Fields FieldMask;
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

        public void Write(NetWriter w)
        {
            w.WriteVarUInt((uint)NetId);
            w.WriteVarUInt(Lifetime);
            w.WriteByte((byte)FieldMask);
            w.WritePosition(Pos);
            w.WriteVector2Half(Vel);
            w.WriteHalf(Rot);
            if ((FieldMask & Fields.Aim) != 0) w.WriteVector2Half(Aim);
            if ((FieldMask & Fields.Status) != 0)
            {
                w.WriteByte(State);
                w.WriteByte(Fire);
                w.WriteByte(Ammo);
            }
            if ((FieldMask & Fields.Vitals) != 0)
            {
                w.WriteHalf(HpFraction);
                w.WriteHalf(ShieldFraction);
                w.WriteHalf(BurnLevel);
            }
        }

        public static EntityStateEntry Read(NetReader r)
        {
            var entry = new EntityStateEntry
            {
                NetId = (int)r.ReadVarUInt(),
                Lifetime = r.ReadVarUInt(),
                FieldMask = (Fields)r.ReadByte(),
                Pos = r.ReadPosition(),
                Vel = r.ReadVector2Half(),
                Rot = r.ReadHalf(),
                State = 255,
                Ammo = 255,
            };
            if ((entry.FieldMask & Fields.Aim) != 0) entry.Aim = r.ReadVector2Half();
            if ((entry.FieldMask & Fields.Status) != 0)
            {
                entry.State = r.ReadByte();
                entry.Fire = r.ReadByte();
                entry.Ammo = r.ReadByte();
            }
            if ((entry.FieldMask & Fields.Vitals) != 0)
            {
                entry.HpFraction = r.ReadHalf();
                entry.ShieldFraction = r.ReadHalf();
                entry.BurnLevel = r.ReadHalf();
            }
            return entry;
        }

        public int EstimatedWireBytes => 24 // worst-case varints + mandatory motion fields
            + ((FieldMask & Fields.Aim) != 0 ? 4 : 0)
            + ((FieldMask & Fields.Status) != 0 ? 3 : 0)
            + ((FieldMask & Fields.Vitals) != 0 ? 6 : 0);
    }

    public struct EntityStateMsg
    {
        public byte Slot;   // authority that produced this batch
        public int SegmentX, SegmentY;
        public uint Epoch;  // segment lease epoch; zero is the host fallback lease
        public uint TimeMs; // its unscaled clock — ordering + jitter-free interpolation
        public System.Collections.Generic.List<EntityStateEntry> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityState);
            w.WriteByte(Slot);
            w.WriteInt(SegmentX);
            w.WriteInt(SegmentY);
            w.WriteUInt(Epoch);
            w.WriteUInt(TimeMs);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var e in Entries) e.Write(w);
        }

        public static EntityStateMsg Read(NetReader r)
        {
            byte slot = r.ReadByte();
            int segmentX = r.ReadInt();
            int segmentY = r.ReadInt();
            uint epoch = r.ReadUInt();
            uint timeMs = r.ReadUInt();
            int n = (int)r.ReadVarUInt();
            var m = new EntityStateMsg { Slot = slot, SegmentX = segmentX, SegmentY = segmentY, Epoch = epoch, TimeMs = timeMs, Entries = new System.Collections.Generic.List<EntityStateEntry>(n) };
            for (int i = 0; i < n; i++) m.Entries.Add(EntityStateEntry.Read(r));
            return m;
        }
    }

    /// <summary>One lease/epoch-scoped group inside a coalesced state datagram.</summary>
    public struct EntityStateGroup
    {
        public int SegmentX, SegmentY;
        public uint Epoch;
        public System.Collections.Generic.List<EntityStateEntry> Entries;

        public void Write(NetWriter w)
        {
            w.WriteInt(SegmentX);
            w.WriteInt(SegmentY);
            w.WriteUInt(Epoch);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var e in Entries) e.Write(w);
        }

        public static EntityStateGroup Read(NetReader r)
        {
            var group = new EntityStateGroup
            {
                SegmentX = r.ReadInt(),
                SegmentY = r.ReadInt(),
                Epoch = r.ReadUInt(),
            };
            int count = (int)r.ReadVarUInt();
            group.Entries = new System.Collections.Generic.List<EntityStateEntry>(count);
            for (int i = 0; i < count; i++) group.Entries.Add(EntityStateEntry.Read(r));
            return group;
        }
    }

    /// <summary>A sender's complete state tick. Segment groups retain their individual epochs,
    /// while one datagram amortizes transport dispatch and receive parsing overhead.</summary>
    public struct EntityStateBundleMsg
    {
        public byte Slot;
        public uint TimeMs;
        public uint Tick;
        public ushort ChunkIndex;
        public ushort ChunkCount;
        public System.Collections.Generic.List<EntityStateGroup> Groups;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityStateBundle);
            w.WriteByte(Slot);
            w.WriteUInt(TimeMs);
            w.WriteUInt(Tick);
            w.WriteUShort(ChunkIndex);
            w.WriteUShort(ChunkCount);
            w.WriteVarUInt((uint)Groups.Count);
            foreach (var group in Groups) group.Write(w);
        }

        public static EntityStateBundleMsg Read(NetReader r)
        {
            var msg = new EntityStateBundleMsg
            {
                Slot = r.ReadByte(),
                TimeMs = r.ReadUInt(),
                Tick = r.ReadUInt(),
                ChunkIndex = r.ReadUShort(),
                ChunkCount = r.ReadUShort(),
            };
            int count = (int)r.ReadVarUInt();
            msg.Groups = new System.Collections.Generic.List<EntityStateGroup>(count);
            for (int i = 0; i < count; i++) msg.Groups.Add(EntityStateGroup.Read(r));
            return msg;
        }
    }

    public enum RuntimeBaselinePurpose : byte
    {
        Interest = 0,
        Handoff = 1,
    }

    /// <summary>Where a baseline entry's state actually came from. Identity existence must never
    /// impersonate a simulator: receivers treat only Live/LastKnown/CoordinatorCache values as
    /// poses a real simulator produced; Generation entries position the object but establish no
    /// authoritative pose.</summary>
    public enum BaselineEntryOrigin : byte
    {
        Live = 0,             // collected from the source's canonical simulating object
        LastKnown = 1,        // source's cached full state from the last real simulator output
        Generation = 2,       // dormant EntityData only — never simulated, state is generation truth
        CoordinatorCache = 3, // host's snapshot cache (lost-owner fallback)
    }

    public static class BaselineEntryFlags
    {
        public const byte OriginMask = 0x03;
        public const byte Prop = 0x04; // non-Unit physics prop: bind without AI puppet expectations

        public static byte Pack(BaselineEntryOrigin origin, bool prop) =>
            (byte)((byte)origin | (prop ? Prop : 0));
        public static BaselineEntryOrigin Origin(byte flags) => (BaselineEntryOrigin)(flags & OriginMask);
        public static bool IsProp(byte flags) => (flags & Prop) != 0;
    }

    public struct RuntimeBaselineRequestMsg
    {
        public uint RequestId;
        public byte SourceSlot;
        public byte TargetSlot;
        public int SegmentX, SegmentY;
        public uint SourceEpoch;
        public uint TargetEpoch;
        public RuntimeBaselinePurpose Purpose;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.RuntimeBaselineRequest);
            w.WriteUInt(RequestId);
            w.WriteByte(SourceSlot); w.WriteByte(TargetSlot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY);
            w.WriteUInt(SourceEpoch); w.WriteUInt(TargetEpoch);
            w.WriteByte((byte)Purpose);
        }

        public static RuntimeBaselineRequestMsg Read(NetReader r) => new RuntimeBaselineRequestMsg
        {
            RequestId = r.ReadUInt(),
            SourceSlot = r.ReadByte(), TargetSlot = r.ReadByte(),
            SegmentX = r.ReadInt(), SegmentY = r.ReadInt(),
            SourceEpoch = r.ReadUInt(), TargetEpoch = r.ReadUInt(),
            Purpose = (RuntimeBaselinePurpose)r.ReadByte(),
        };
    }

    public struct RuntimeBaselineMsg
    {
        public uint RequestId;
        public byte SourceSlot;
        public byte TargetSlot;
        public int SegmentX, SegmentY;
        public uint SourceEpoch;
        public uint TargetEpoch;
        public uint Tick;
        public RuntimeBaselinePurpose Purpose;
        public ulong RosterDigest;
        public System.Collections.Generic.List<EntityStateEntry> Entries;
        public System.Collections.Generic.List<string> EntityTypes;
        public System.Collections.Generic.List<byte> EntryFlags; // BaselineEntryFlags per entry

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.RuntimeBaseline);
            w.WriteUInt(RequestId);
            w.WriteByte(SourceSlot); w.WriteByte(TargetSlot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY);
            w.WriteUInt(SourceEpoch); w.WriteUInt(TargetEpoch); w.WriteUInt(Tick);
            w.WriteByte((byte)Purpose);
            w.WriteULong(RosterDigest);
            w.WriteVarUInt((uint)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                w.WriteString(EntityTypes != null && i < EntityTypes.Count ? EntityTypes[i] : string.Empty);
                w.WriteByte(EntryFlags != null && i < EntryFlags.Count ? EntryFlags[i] : (byte)0);
                var full = Entries[i];
                full.FieldMask = EntityStateEntry.Fields.Full;
                full.Write(w);
            }
        }

        public static RuntimeBaselineMsg Read(NetReader r)
        {
            var msg = new RuntimeBaselineMsg
            {
                RequestId = r.ReadUInt(),
                SourceSlot = r.ReadByte(), TargetSlot = r.ReadByte(),
                SegmentX = r.ReadInt(), SegmentY = r.ReadInt(),
                SourceEpoch = r.ReadUInt(), TargetEpoch = r.ReadUInt(), Tick = r.ReadUInt(),
                Purpose = (RuntimeBaselinePurpose)r.ReadByte(),
                RosterDigest = r.ReadULong(),
            };
            int count = (int)r.ReadVarUInt();
            msg.Entries = new System.Collections.Generic.List<EntityStateEntry>(count);
            msg.EntityTypes = new System.Collections.Generic.List<string>(count);
            msg.EntryFlags = new System.Collections.Generic.List<byte>(count);
            for (int i = 0; i < count; i++)
            {
                msg.EntityTypes.Add(r.ReadString());
                msg.EntryFlags.Add(r.ReadByte());
                msg.Entries.Add(EntityStateEntry.Read(r));
            }
            return msg;
        }
    }

    public struct RuntimeBaselineAckMsg
    {
        public uint RequestId;
        public byte TargetSlot;
        public int SegmentX, SegmentY;
        public uint TargetEpoch;
        public RuntimeBaselinePurpose Purpose;
        public ulong RosterDigest;
        public bool Installed;
        public int ExpectedCount;
        public int MaterializedCount;
        public System.Collections.Generic.List<int> MissingNetIds;

        public bool Complete => Installed && MaterializedCount == ExpectedCount
            && (MissingNetIds == null || MissingNetIds.Count == 0);

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.RuntimeBaselineAck);
            w.WriteUInt(RequestId); w.WriteByte(TargetSlot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY); w.WriteUInt(TargetEpoch);
            w.WriteByte((byte)Purpose);
            w.WriteULong(RosterDigest);
            w.WriteBool(Installed);
            w.WriteVarUInt((uint)ExpectedCount);
            w.WriteVarUInt((uint)MaterializedCount);
            int missing = MissingNetIds?.Count ?? 0;
            w.WriteVarUInt((uint)missing);
            for (int i = 0; i < missing; i++) w.WriteVarUInt((uint)MissingNetIds[i]);
        }

        public static RuntimeBaselineAckMsg Read(NetReader r)
        {
            var msg = new RuntimeBaselineAckMsg
            {
                RequestId = r.ReadUInt(), TargetSlot = r.ReadByte(),
                SegmentX = r.ReadInt(), SegmentY = r.ReadInt(), TargetEpoch = r.ReadUInt(),
                Purpose = (RuntimeBaselinePurpose)r.ReadByte(),
                RosterDigest = r.ReadULong(),
                Installed = r.ReadBool(),
                ExpectedCount = (int)r.ReadVarUInt(),
                MaterializedCount = (int)r.ReadVarUInt(),
            };
            int missing = (int)r.ReadVarUInt();
            msg.MissingNetIds = new System.Collections.Generic.List<int>(missing);
            for (int i = 0; i < missing; i++) msg.MissingNetIds.Add((int)r.ReadVarUInt());
            return msg;
        }
    }

    public struct DirectRouteMsg
    {
        public byte OwnerSlot;
        public byte TargetSlot;
        public int SegmentX, SegmentY;
        public uint Epoch;
        public bool Enabled;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.DirectRoute);
            w.WriteByte(OwnerSlot); w.WriteByte(TargetSlot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY); w.WriteUInt(Epoch); w.WriteBool(Enabled);
        }

        public static DirectRouteMsg Read(NetReader r) => new DirectRouteMsg
        {
            OwnerSlot = r.ReadByte(), TargetSlot = r.ReadByte(),
            SegmentX = r.ReadInt(), SegmentY = r.ReadInt(), Epoch = r.ReadUInt(), Enabled = r.ReadBool(),
        };
    }

    public struct DirectRoutePulseMsg
    {
        public byte OwnerSlot;
        public byte TargetSlot;
        public int SegmentX, SegmentY;
        public uint Epoch;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.DirectRoutePulse);
            w.WriteByte(OwnerSlot); w.WriteByte(TargetSlot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY); w.WriteUInt(Epoch);
        }

        public static DirectRoutePulseMsg Read(NetReader r) => new DirectRoutePulseMsg
        {
            OwnerSlot = r.ReadByte(), TargetSlot = r.ReadByte(),
            SegmentX = r.ReadInt(), SegmentY = r.ReadInt(), Epoch = r.ReadUInt(),
        };
    }

    public struct EntityBoundaryHandoffMsg
    {
        public byte SourceSlot;
        public byte TargetSlot;
        public int FromX, FromY, ToX, ToY;
        public uint FromEpoch, ToEpoch;
        public EntityStateEntry Entry;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityBoundaryHandoff);
            w.WriteByte(SourceSlot); w.WriteByte(TargetSlot);
            w.WriteInt(FromX); w.WriteInt(FromY); w.WriteUInt(FromEpoch);
            w.WriteInt(ToX); w.WriteInt(ToY); w.WriteUInt(ToEpoch);
            var full = Entry; full.FieldMask = EntityStateEntry.Fields.Full; full.Write(w);
        }

        public static EntityBoundaryHandoffMsg Read(NetReader r) => new EntityBoundaryHandoffMsg
        {
            SourceSlot = r.ReadByte(), TargetSlot = r.ReadByte(),
            FromX = r.ReadInt(), FromY = r.ReadInt(), FromEpoch = r.ReadUInt(),
            ToX = r.ReadInt(), ToY = r.ReadInt(), ToEpoch = r.ReadUInt(),
            Entry = EntityStateEntry.Read(r),
        };
    }

    public struct EntityBaselineEntry
    {
        public int NetId;
        public UnityEngine.Vector2 Pos;
    }

    public struct EntityBaselineMsg
    {
        public ushort Start, Total;
        public System.Collections.Generic.List<EntityBaselineEntry> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityBaseline);
            w.WriteUShort(Start); w.WriteUShort(Total); w.WriteVarUInt((uint)Entries.Count);
            foreach (var e in Entries) { w.WriteVarUInt((uint)e.NetId); w.WritePosition(e.Pos); }
        }

        public static EntityBaselineMsg Read(NetReader r)
        {
            var m = new EntityBaselineMsg { Start = r.ReadUShort(), Total = r.ReadUShort() };
            int n = (int)r.ReadVarUInt();
            m.Entries = new System.Collections.Generic.List<EntityBaselineEntry>(n);
            for (int i = 0; i < n; i++) m.Entries.Add(new EntityBaselineEntry { NetId = (int)r.ReadVarUInt(), Pos = r.ReadPosition() });
            return m;
        }
    }

    public struct TerrainDigestMsg
    {
        public uint Revision;
        public ulong Hash;
        public int Count;
        public void Write(NetWriter w) { w.WriteMsgType(MsgType.TerrainDigest); w.WriteUInt(Revision); w.WriteULong(Hash); w.WriteInt(Count); }
        public static TerrainDigestMsg Read(NetReader r) => new TerrainDigestMsg { Revision = r.ReadUInt(), Hash = r.ReadULong(), Count = r.ReadInt() };
    }

    public struct TerrainRepairRequestMsg
    {
        public uint Revision;
        public ulong Hash;
        public void Write(NetWriter w) { w.WriteMsgType(MsgType.TerrainRepairRequest); w.WriteUInt(Revision); w.WriteULong(Hash); }
        public static TerrainRepairRequestMsg Read(NetReader r) => new TerrainRepairRequestMsg { Revision = r.ReadUInt(), Hash = r.ReadULong() };
    }

    public struct TerrainRepairChunkMsg
    {
        public uint Revision;
        public ulong Hash;
        public int Start, Total;
        public System.Collections.Generic.List<(int index, byte type)> Cells;
        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.TerrainRepairChunk); w.WriteUInt(Revision); w.WriteULong(Hash);
            w.WriteInt(Start); w.WriteInt(Total); w.WriteVarUInt((uint)Cells.Count);
            foreach (var c in Cells) { w.WriteInt(c.index); w.WriteByte(c.type); }
        }
        public static TerrainRepairChunkMsg Read(NetReader r)
        {
            var m = new TerrainRepairChunkMsg { Revision = r.ReadUInt(), Hash = r.ReadULong(), Start = r.ReadInt(), Total = r.ReadInt() };
            int n = (int)r.ReadVarUInt(); m.Cells = new System.Collections.Generic.List<(int, byte)>(n);
            for (int i = 0; i < n; i++) m.Cells.Add((r.ReadInt(), r.ReadByte()));
            return m;
        }
    }

    public struct SegmentLeaseMsg
    {
        public int X, Y;
        public byte Owner;
        public uint Epoch;
        public byte Phase; // 0 prepare, 1 commit, 2 cancel

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.SegmentLease);
            w.WriteInt(X); w.WriteInt(Y); w.WriteByte(Owner); w.WriteUInt(Epoch); w.WriteByte(Phase);
        }

        public static SegmentLeaseMsg Read(NetReader r) => new SegmentLeaseMsg
        {
            X = r.ReadInt(), Y = r.ReadInt(), Owner = r.ReadByte(), Epoch = r.ReadUInt(), Phase = r.ReadByte(),
        };
    }

    /// <summary>Full set of segments this peer's game is currently streaming (its EGM
    /// activeSegments). Full-set semantics keep it idempotent and self-healing; Rev orders
    /// reports so a stale one can never regress the host's view.</summary>
    public struct ResidencyReportMsg
    {
        public byte Slot;
        public uint Rev;
        public System.Collections.Generic.List<UnityEngine.Vector2Int> Segments;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.ResidencyReport);
            w.WriteByte(Slot);
            w.WriteUInt(Rev);
            int count = Segments?.Count ?? 0;
            w.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteInt(Segments[i].x);
                w.WriteInt(Segments[i].y);
            }
        }

        public static ResidencyReportMsg Read(NetReader r)
        {
            var msg = new ResidencyReportMsg { Slot = r.ReadByte(), Rev = r.ReadUInt() };
            int count = (int)r.ReadVarUInt();
            msg.Segments = new System.Collections.Generic.List<UnityEngine.Vector2Int>(count);
            for (int i = 0; i < count; i++)
                msg.Segments.Add(new UnityEngine.Vector2Int(r.ReadInt(), r.ReadInt()));
            return msg;
        }
    }

    /// <summary>The release edge (I-10): an owner's final entity states for a segment its game
    /// is unloading, sent BEFORE the objects are destroyed. The host stores them as canonical
    /// dormant state, relays to every peer (host-migration input), and moves the lease to
    /// Dormant.</summary>
    public struct SegmentDormancyCommitMsg
    {
        public byte Slot;
        public int SegmentX, SegmentY;
        public uint Epoch;
        public ulong RosterDigest;
        public System.Collections.Generic.List<EntityStateEntry> Entries;
        public System.Collections.Generic.List<byte> EntryFlags;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.SegmentDormancyCommit);
            w.WriteByte(Slot);
            w.WriteInt(SegmentX); w.WriteInt(SegmentY);
            w.WriteUInt(Epoch);
            w.WriteULong(RosterDigest);
            int count = Entries?.Count ?? 0;
            w.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteByte(EntryFlags != null && i < EntryFlags.Count ? EntryFlags[i] : (byte)0);
                var full = Entries[i];
                full.FieldMask = EntityStateEntry.Fields.Full;
                full.Write(w);
            }
        }

        public static SegmentDormancyCommitMsg Read(NetReader r)
        {
            var msg = new SegmentDormancyCommitMsg
            {
                Slot = r.ReadByte(),
                SegmentX = r.ReadInt(), SegmentY = r.ReadInt(),
                Epoch = r.ReadUInt(),
                RosterDigest = r.ReadULong(),
            };
            int count = (int)r.ReadVarUInt();
            msg.Entries = new System.Collections.Generic.List<EntityStateEntry>(count);
            msg.EntryFlags = new System.Collections.Generic.List<byte>(count);
            for (int i = 0; i < count; i++)
            {
                msg.EntryFlags.Add(r.ReadByte());
                msg.Entries.Add(EntityStateEntry.Read(r));
            }
            return msg;
        }
    }

    /// <summary>Late-join replay of the coordinator's canonical state cache: last known state
    /// (vitals, poses, velocities) for every entity that was ever simulated, chunked.</summary>
    public struct DormantStateMsg
    {
        public ushort Start, Total;
        public System.Collections.Generic.List<EntityStateEntry> Entries;
        public System.Collections.Generic.List<byte> EntryFlags;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.DormantState);
            w.WriteUShort(Start); w.WriteUShort(Total);
            int count = Entries?.Count ?? 0;
            w.WriteVarUInt((uint)count);
            for (int i = 0; i < count; i++)
            {
                w.WriteByte(EntryFlags != null && i < EntryFlags.Count ? EntryFlags[i] : (byte)0);
                var full = Entries[i];
                full.FieldMask = EntityStateEntry.Fields.Full;
                full.Write(w);
            }
        }

        public static DormantStateMsg Read(NetReader r)
        {
            var msg = new DormantStateMsg { Start = r.ReadUShort(), Total = r.ReadUShort() };
            int count = (int)r.ReadVarUInt();
            msg.Entries = new System.Collections.Generic.List<EntityStateEntry>(count);
            msg.EntryFlags = new System.Collections.Generic.List<byte>(count);
            for (int i = 0; i < count; i++)
            {
                msg.EntryFlags.Add(r.ReadByte());
                msg.Entries.Add(EntityStateEntry.Read(r));
            }
            return msg;
        }
    }

    public struct KillLedgerMsg
    {
        public System.Collections.Generic.List<(int netId, uint lifetime, uint revision)> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.KillLedger);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var entry in Entries)
            {
                w.WriteVarUInt((uint)entry.netId);
                w.WriteVarUInt(entry.lifetime);
                w.WriteVarUInt(entry.revision);
            }
        }

        public static KillLedgerMsg Read(NetReader r)
        {
            int n = (int)r.ReadVarUInt();
            var m = new KillLedgerMsg
            {
                Entries = new System.Collections.Generic.List<(int, uint, uint)>(n),
            };
            for (int i = 0; i < n; i++)
                m.Entries.Add(((int)r.ReadVarUInt(), r.ReadVarUInt(), r.ReadVarUInt()));
            return m;
        }
    }

    public struct EntityKilledMsg
    {
        public int NetId;
        public uint Lifetime;
        public uint MutationRevision;
        public byte KillerSlot;
        public bool HasPosition;
        public UnityEngine.Vector2 Position;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityKilled);
            w.WriteVarUInt((uint)NetId);
            w.WriteVarUInt(Lifetime);
            w.WriteVarUInt(MutationRevision);
            w.WriteByte(KillerSlot);
            w.WriteBool(HasPosition);
            if (HasPosition) w.WritePosition(Position);
        }

        public static EntityKilledMsg Read(NetReader r)
        {
            var msg = new EntityKilledMsg
            {
                NetId = (int)r.ReadVarUInt(),
                Lifetime = r.ReadVarUInt(),
                MutationRevision = r.ReadVarUInt(),
                KillerSlot = r.ReadByte(),
                HasPosition = r.ReadBool(),
            };
            if (msg.HasPosition) msg.Position = r.ReadPosition();
            return msg;
        }
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
        public uint Revision; // zero = client proposal or catch-up chunk; host live order starts at 1
        public System.Collections.Generic.List<(int index, byte type)> Cells;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.CellDiff);
            w.WriteUInt(Revision);
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
            uint revision = r.ReadUInt();
            int count = (int)r.ReadVarUInt();
            var cells = new System.Collections.Generic.List<(int, byte)>(count);
            int prev = 0;
            for (int i = 0; i < count; i++)
            {
                prev += (int)r.ReadVarUInt();
                cells.Add((prev, r.ReadByte()));
            }
            return new CellDiffMsg { Revision = revision, Cells = cells };
        }
    }

    public struct EntityFireMsg
    {
        public int NetId;
        public uint Lifetime;
        public byte SourceSlot;               // simulator that originated this burst
        public int SegmentX, SegmentY;        // lease containing BodyPos at fire time
        public uint Epoch;                    // committed lease epoch at fire time
        public uint ShotId;                   // origin-slot-prefixed, monotonic burst identity
        public UnityEngine.Vector2 Pos;
        public UnityEngine.Vector2 Dir;
        public UnityEngine.Vector2 BodyPos; // shooter's body position at fire time — lets the
                                            // replay re-anchor the muzzle to the local puppet
        public byte TargetSlot;             // player the shooter is targeting (homing), 255 = none
        public int Seed;                    // RNG seed used for this burst tick

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntityFire);
            w.WriteVarUInt((uint)NetId);
            w.WriteVarUInt(Lifetime);
            w.WriteByte(SourceSlot);
            w.WriteInt(SegmentX);
            w.WriteInt(SegmentY);
            w.WriteUInt(Epoch);
            w.WriteUInt(ShotId);
            w.WritePosition(Pos);
            w.WriteVector2Half(Dir);
            w.WritePosition(BodyPos);
            w.WriteByte(TargetSlot);
            w.WriteInt(Seed);
        }

        public static EntityFireMsg Read(NetReader r) => new EntityFireMsg
        {
            NetId = (int)r.ReadVarUInt(),
            Lifetime = r.ReadVarUInt(),
            SourceSlot = r.ReadByte(),
            SegmentX = r.ReadInt(),
            SegmentY = r.ReadInt(),
            Epoch = r.ReadUInt(),
            ShotId = r.ReadUInt(),
            Pos = r.ReadPosition(),
            Dir = r.ReadVector2Half(),
            BodyPos = r.ReadPosition(),
            TargetSlot = r.ReadByte(),
            Seed = r.ReadInt(),
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
        public uint Lifetime;
        public string EntityId;
        public UnityEngine.Vector2 Pos;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.EntitySpawned);
            w.WriteVarUInt((uint)NetId);
            w.WriteByte(OwnerSlot);
            w.WriteVarUInt(Lifetime);
            w.WriteString(EntityId);
            w.WritePosition(Pos);
        }

        public static EntitySpawnedMsg Read(NetReader r) => new EntitySpawnedMsg
        {
            NetId = (int)r.ReadVarUInt(),
            OwnerSlot = r.ReadByte(),
            Lifetime = r.ReadVarUInt(),
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
        public uint Lifetime;
        public string EntityId; // SavablesCollection prefab key
        public UnityEngine.Vector2 Pos;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.MinionSpawned);
            w.WriteVarUInt((uint)NetId);
            w.WriteByte(OwnerSlot);
            w.WriteVarUInt(Lifetime);
            w.WriteString(EntityId);
            w.WritePosition(Pos);
        }

        public static MinionSpawnedMsg Read(NetReader r) => new MinionSpawnedMsg
        {
            NetId = (int)r.ReadVarUInt(),
            OwnerSlot = r.ReadByte(),
            Lifetime = r.ReadVarUInt(),
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

    // Any player permanently revealed a station/POI on the map (MapIconManager overdrawn icon).
    // Synced by entity so every player's map marks the SAME location discovered, immediately,
    // without waiting for them to open their own map menu.
    public struct MapDiscoveredMsg
    {
        public int NetId;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.MapDiscovered);
            w.WriteVarUInt((uint)NetId);
        }

        public static MapDiscoveredMsg Read(NetReader r) => new MapDiscoveredMsg { NetId = (int)r.ReadVarUInt() };
    }

    public struct IdResolveRequestMsg
    {
        public System.Collections.Generic.List<int> NetIds;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.IdResolveRequest);
            w.WriteVarUInt((uint)NetIds.Count);
            foreach (var id in NetIds) w.WriteVarUInt((uint)id);
        }

        public static IdResolveRequestMsg Read(NetReader r)
        {
            int n = (int)r.ReadVarUInt();
            var m = new IdResolveRequestMsg { NetIds = new System.Collections.Generic.List<int>(n) };
            for (int i = 0; i < n; i++) m.NetIds.Add((int)r.ReadVarUInt());
            return m;
        }
    }

    public struct IdResolveEntry
    {
        public int NetId;
        public uint TypeHash; // FNV-1a of the entityId string
        public int Qx, Qy;    // position quantized to 0.5u (same grid as the fingerprints)
    }

    public struct IdResolveReplyMsg
    {
        public System.Collections.Generic.List<IdResolveEntry> Entries;

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.IdResolveReply);
            w.WriteVarUInt((uint)Entries.Count);
            foreach (var e in Entries)
            {
                w.WriteVarUInt((uint)e.NetId);
                w.WriteUInt(e.TypeHash);
                w.WriteInt(e.Qx);
                w.WriteInt(e.Qy);
            }
        }

        public static IdResolveReplyMsg Read(NetReader r)
        {
            int n = (int)r.ReadVarUInt();
            var m = new IdResolveReplyMsg { Entries = new System.Collections.Generic.List<IdResolveEntry>(n) };
            for (int i = 0; i < n; i++)
            {
                m.Entries.Add(new IdResolveEntry
                {
                    NetId = (int)r.ReadVarUInt(),
                    TypeHash = r.ReadUInt(),
                    Qx = r.ReadInt(),
                    Qy = r.ReadInt(),
                });
            }
            return m;
        }
    }

    public struct TerrainSyncMsg
    {
        public const byte PhaseBegin = 0;
        public const byte PhaseEnd = 1;

        public byte Phase;
        public int Chunks; // begin: chunks queued; end: chunks actually sent
        public int Cells;  // begin: ledger cells at queue time; end: cells actually sent

        public void Write(NetWriter w)
        {
            w.WriteMsgType(MsgType.TerrainSync);
            w.WriteByte(Phase);
            w.WriteVarUInt((uint)Chunks);
            w.WriteVarUInt((uint)Cells);
        }

        public static TerrainSyncMsg Read(NetReader r) => new TerrainSyncMsg
        {
            Phase = r.ReadByte(),
            Chunks = (int)r.ReadVarUInt(),
            Cells = (int)r.ReadVarUInt(),
        };
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
