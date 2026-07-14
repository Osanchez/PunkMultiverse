using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Core
{
    internal enum PerfPhase
    {
        Unknown,
        UnityBetweenUpdates,
        NetSessionUpdate,
        SteamPump,
        TransportPoll,
        TransportDrain,
        AutoFly,
        ShipSync,
        WorldFlush,
        WorldTick,
        EnemySync,
        DamageWatchdog,
        ModuleGrid,
        Fog,
        Economy,
        RunSave,
        Diagnostics,
        Authority,
        PartyWipe,
        InstrumentationReport,
        HarmonyBase = 100,
    }

    /// <summary>Coordinates whole-frame, GC, object-count, and watchdog diagnostics. Hot calls only
    /// update preallocated storage; formatting and Unity object scans happen at report boundaries.</summary>
    internal static class RuntimeInstrumentation
    {
        private const int HistogramMaxMs = 300;
        private static readonly long[] FrameHistogram = new long[HistogramMaxMs + 2];
        private static readonly MethodInfo AllocatedBytesMethod = typeof(GC).GetMethod(
            "GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);

        private static bool _initialized;
        private static bool _inMeasuredState;
        private static int _currentPhase;
        private static float _nextReportAt;
        private static float _nextObjectScanAt;
        private static long _reportStartedTicks;
        private static long _frameCount;
        private static double _frameSumMs;
        private static double _frameMaxMs;
        private static int _lastGen0, _lastGen1, _lastGen2;
        private static long _lastHeap;
        private static long _lastAllocated;
        private static bool _gcBaselined;
        private static bool _slowScansDisabled;
        private static long _lastCellChanges;
        private static long _lastVisualSpawns;
        private static long _lastStateBundlesSent, _lastStateGroupsSent, _lastStateEntriesSent, _lastStateBytesSent;
        private static long _lastStateBundlesReceived, _lastStateGroupsReceived, _lastStateEntriesReceived;
        private static long _lastStateGroupsFiltered, _lastStateEntriesFiltered;
        private static int _lastFirstLifetimes, _lastReenteredLifetimes, _lastOverlappingLifetimes, _lastRetiredLifetimes;
        private static bool _populationPressureWarned;
        private static readonly System.Collections.Generic.Dictionary<string, int> PopulationTypes =
            new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.Dictionary<string, int> PopulationSegments =
            new System.Collections.Generic.Dictionary<string, int>();
        private static readonly System.Collections.Generic.Dictionary<string, int> ReplacementTypes =
            new System.Collections.Generic.Dictionary<string, int>();

        internal static double MonoSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        internal static void Initialize(Thread mainThread)
        {
            if (_initialized) return;
            _initialized = true;
            _currentPhase = (int)PerfPhase.UnityBetweenUpdates;
            MainThreadWatchdog.Initialize(mainThread);
            ApplyWatchdogConfig();
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Hitch] watchdog enabled={0} threshold={1}ms repeat={2}ms main-stack={3}",
                NetConfig.HitchWatchdog.Value, Clamp(NetConfig.HitchThresholdMs.Value, 100, 5000),
                Clamp(NetConfig.HitchRepeatMs.Value, 500, 30000),
                !NetConfig.CaptureHitchStack.Value ? "disabled"
                    : (MainThreadWatchdog.StackCaptureSupported ? "supported" : "unsupported")));
            ResetRun();
        }

        internal static void Shutdown()
        {
            if (!_initialized) return;
            MainThreadWatchdog.Shutdown();
            _initialized = false;
        }

        internal static void ResetRun()
        {
            Array.Clear(FrameHistogram, 0, FrameHistogram.Length);
            _frameCount = 0;
            _frameSumMs = 0;
            _frameMaxMs = 0;
            _nextReportAt = 0;
            _nextObjectScanAt = 0;
            _reportStartedTicks = Stopwatch.GetTimestamp();
            _gcBaselined = false;
            _slowScansDisabled = false;
            _lastCellChanges = InstrumentationCounters.CellChanges;
            _lastVisualSpawns = InstrumentationCounters.VisualSpawns;
            _lastStateBundlesSent = InstrumentationCounters.StateBundlesSent;
            _lastStateGroupsSent = InstrumentationCounters.StateGroupsSent;
            _lastStateEntriesSent = InstrumentationCounters.StateEntriesSent;
            _lastStateBytesSent = InstrumentationCounters.StateBytesSent;
            _lastStateBundlesReceived = InstrumentationCounters.StateBundlesReceived;
            _lastStateGroupsReceived = InstrumentationCounters.StateGroupsReceived;
            _lastStateEntriesReceived = InstrumentationCounters.StateEntriesReceived;
            _lastStateGroupsFiltered = InstrumentationCounters.StateGroupsFiltered;
            _lastStateEntriesFiltered = InstrumentationCounters.StateEntriesFiltered;
            _lastFirstLifetimes = EnemySync.FirstLifetimeCount;
            _lastReenteredLifetimes = EnemySync.ReenteredLifetimeCount;
            _lastOverlappingLifetimes = EnemySync.OverlappingLifetimeCount;
            _lastRetiredLifetimes = EnemySync.RetiredLifetimeCount;
            _populationPressureWarned = false;
            Warned.Clear();
            PatchProfiler.Reset();
        }

        internal static void UpdateStart(SessionState state)
        {
            if (!_initialized) return;
            ApplyWatchdogConfig();
            MainThreadWatchdog.PublishState(state);
            SetPhase(PerfPhase.NetSessionUpdate);
            MainThreadWatchdog.Beat(_currentPhase);
            _inMeasuredState = state == SessionState.Loading || state == SessionState.InGame;
            if (NetProfiler.Enabled && _inMeasuredState)
                SampleFrame(Time.unscaledDeltaTime * 1000.0);
        }

        internal static void UpdateEnd(SessionState state)
        {
            if (!_initialized) return;
            if (NetProfiler.Enabled && _inMeasuredState)
            {
                PatchProfiler.EndFrame(MonoSeconds);
                if (Time.unscaledTime >= _nextReportAt)
                {
                    SetPhase(PerfPhase.InstrumentationReport);
                    Report(state);
                }
            }
            _inMeasuredState = false;
            SetPhase(PerfPhase.UnityBetweenUpdates);
            MainThreadWatchdog.Beat(_currentPhase);
        }

        internal static void SetPhase(PerfPhase phase)
        {
            _currentPhase = (int)phase;
            MainThreadWatchdog.Phase(_currentPhase);
        }

        internal static int EnterPatchPhase(PatchId id)
        {
            int previous = _currentPhase;
            _currentPhase = (int)PerfPhase.HarmonyBase + (int)id;
            MainThreadWatchdog.Phase(_currentPhase);
            return previous;
        }

        internal static void ExitPatchPhase(int previous)
        {
            _currentPhase = previous;
            MainThreadWatchdog.Phase(previous);
        }

        internal static string PhaseName(int phase)
        {
            if (phase >= (int)PerfPhase.HarmonyBase
                && phase < (int)PerfPhase.HarmonyBase + (int)PatchId.Count)
                return "Harmony." + ((PatchId)(phase - (int)PerfPhase.HarmonyBase)).ToString();
            switch ((PerfPhase)phase)
            {
                case PerfPhase.UnityBetweenUpdates: return "Unity.BetweenUpdates";
                case PerfPhase.NetSessionUpdate: return "NetSession.Update";
                case PerfPhase.SteamPump: return "Steam.Pump";
                case PerfPhase.TransportPoll: return "Transport.Poll";
                case PerfPhase.TransportDrain: return "Transport.Drain";
                case PerfPhase.AutoFly: return "AutoFly";
                case PerfPhase.ShipSync: return "ShipSync";
                case PerfPhase.WorldFlush: return "WorldSync.Flush";
                case PerfPhase.WorldTick: return "WorldSync.Tick";
                case PerfPhase.EnemySync: return "EnemySync";
                case PerfPhase.DamageWatchdog: return "DamageWatchdog";
                case PerfPhase.ModuleGrid: return "ModuleGrid";
                case PerfPhase.Fog: return "Fog";
                case PerfPhase.Economy: return "Economy";
                case PerfPhase.RunSave: return "RunSave";
                case PerfPhase.Diagnostics: return "Diagnostics";
                case PerfPhase.Authority: return "Authority";
                case PerfPhase.PartyWipe: return "PartyWipe";
                case PerfPhase.InstrumentationReport: return "Instrumentation.Report";
                default: return "Unknown";
            }
        }

        private static void SampleFrame(double ms)
        {
            if (ms < 0 || double.IsNaN(ms) || double.IsInfinity(ms)) return;
            _frameCount++;
            _frameSumMs += ms;
            if (ms > _frameMaxMs) _frameMaxMs = ms;
            int bucket = ms > HistogramMaxMs ? HistogramMaxMs + 1 : Math.Max(0, (int)Math.Ceiling(ms));
            FrameHistogram[bucket]++;
        }

        private static void Report(SessionState state)
        {
            float interval = Math.Max(1f, Math.Min(30f, NetConfig.ProfileReportInterval.Value));
            _nextReportAt = Time.unscaledTime + interval;
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(0.001, (nowTicks - _reportStartedTicks) / (double)Stopwatch.Frequency);
            double mono = nowTicks / (double)Stopwatch.Frequency;
            _reportStartedTicks = nowTicks;

            try { ReportFrames(mono); }
            catch (Exception e) { WarnOnce("frame", e); }
            try { ReportGc(mono, elapsed); }
            catch (Exception e) { WarnOnce("gc", e); }
            try { PatchProfiler.Report(mono, elapsed); }
            catch (Exception e) { WarnOnce("patch", e); }
            try { ReportCounts(mono, elapsed); }
            catch (Exception e) { WarnOnce("counts", e); }
            try { ReportStateFlow(mono, elapsed); }
            catch (Exception e) { WarnOnce("state-flow", e); }
            try { ReportPopulation(mono, elapsed); }
            catch (Exception e) { WarnOnce("population", e); }
            try { MaybeScanObjects(mono); }
            catch (Exception e) { WarnOnce("scan", e); }
        }

        private static void ReportFrames(double mono)
        {
            if (_frameCount == 0) return;
            long target = (long)Math.Ceiling(_frameCount * 0.99);
            long seen = 0;
            int p99 = HistogramMaxMs + 1;
            for (int i = 0; i < FrameHistogram.Length; i++)
            {
                seen += FrameHistogram[i];
                if (seen >= target) { p99 = i; break; }
            }
            double avg = _frameSumMs / _frameCount;
            var sb = new StringBuilder(256);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "[Frame] mono={0:0.000}s n={1} avg={2:0.0}ms p99={3}ms max={4:0.0}ms fps={5:0.0} over=",
                mono, _frameCount, avg, p99 > HistogramMaxMs ? ">300" : p99.ToString(CultureInfo.InvariantCulture),
                _frameMaxMs, avg > 0 ? 1000.0 / avg : 0);
            AppendOver(sb, 17, "16.7"); sb.Append(',');
            AppendOver(sb, 34, "33.3"); sb.Append(',');
            AppendOver(sb, 50, "50"); sb.Append(',');
            AppendOver(sb, 100, "100"); sb.Append(',');
            AppendOver(sb, 250, "250");
            Plugin.Log.LogInfo(sb.ToString());
            Array.Clear(FrameHistogram, 0, FrameHistogram.Length);
            _frameCount = 0;
            _frameSumMs = 0;
            _frameMaxMs = 0;
        }

        private static void AppendOver(StringBuilder sb, int threshold, string label)
        {
            long count = 0;
            for (int i = threshold; i < FrameHistogram.Length; i++) count += FrameHistogram[i];
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0}ms:{1}", label, count);
        }

        private static void ReportGc(double mono, double elapsed)
        {
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long heap = GC.GetTotalMemory(false);
            long allocated = TryAllocatedBytes(out bool supported);
            if (!_gcBaselined)
            {
                _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2;
                _lastHeap = heap; _lastAllocated = allocated;
                _gcBaselined = true;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[GC] mono={0:0.000}s baseline heap={1:0.0}MiB alloc={2}", mono,
                    heap / 1048576.0, supported ? "supported" : "unsupported"));
                return;
            }

            long allocDelta = supported && allocated >= _lastAllocated ? allocated - _lastAllocated : 0;
            string allocText = supported
                ? string.Format(CultureInfo.InvariantCulture, "mainAlloc={0:0.0}MiB rate={1:0.0}MiB/s",
                    allocDelta / 1048576.0, allocDelta / elapsed / 1048576.0)
                : "alloc=unsupported";
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[GC] mono={0:0.000}s gen={1}/{2}/{3} heap={4:0.0}MiB heapDelta={5:+0.0;-0.0;0.0}MiB {6}",
                mono, Math.Max(0, g0 - _lastGen0), Math.Max(0, g1 - _lastGen1), Math.Max(0, g2 - _lastGen2),
                heap / 1048576.0, (heap - _lastHeap) / 1048576.0, allocText));
            _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2;
            _lastHeap = heap; _lastAllocated = allocated;
        }

        private static long TryAllocatedBytes(out bool supported)
        {
            supported = AllocatedBytesMethod != null;
            if (!supported) return 0;
            try { return (long)AllocatedBytesMethod.Invoke(null, null); }
            catch { supported = false; return 0; }
        }

        private static void ReportCounts(double mono, double elapsed)
        {
            long cells = InstrumentationCounters.CellChanges;
            long visualSpawns = InstrumentationCounters.VisualSpawns;
            long cellDelta = Math.Max(0, cells - _lastCellChanges);
            long visualDelta = Math.Max(0, visualSpawns - _lastVisualSpawns);
            _lastCellChanges = cells;
            _lastVisualSpawns = visualSpawns;
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Counts] mono={0:0.000}s remoteShips={1} remoteEntities={2} visualIds={3} owners={4} fixed={5} killed={6} pendingCells={7} cells={8:0.#}/s visualSpawns={9:0.#}/s leases={10} pendingLeases={11} commits={12} acks={13} staleStateDrops={14} positionSegmentDrops={31} authorityDrops={32} authorityRebaselines={35} identityOverlaps={15} activeQuarantines={16} duplicateFireDrops={17} duplicateImpactDrops={18} staleFireDrops={19} damageRequestDedupes={20} starvedPuppetFrames={21} starvedRequests={36} availabilityPromotions={37} availabilityDeferrals={38} localAuthorityRepairs={39} killLedgerIds={22} dormantDamage={23}/{24} terrain={25}/{26:X16}/{27} terrainMismatch={28} repairs={29}/{30} disconnectDespawns={33} stationRespawns={34}",
                mono, InstrumentationCounters.RemoteShips, InstrumentationCounters.RemoteEntities,
                ProjectileSync.VisualProjectileCount, EnemySync.Owners.Count, EnemySync.FixedOwners.Count,
                EnemySync.KilledCount, WorldSync.PendingCount, cellDelta / elapsed,
                visualDelta / elapsed, AuthorityManager.CommittedLeaseCount, AuthorityManager.PendingLeaseCount,
                InstrumentationCounters.LeaseCommits, InstrumentationCounters.LeaseAcks,
                InstrumentationCounters.StaleStateDrops, InstrumentationCounters.DuplicatesPrevented,
                InstrumentationCounters.ActiveDuplicateLifetimes, InstrumentationCounters.DuplicateFireDrops,
                InstrumentationCounters.DuplicateImpactDrops, InstrumentationCounters.StaleFireDrops,
                InstrumentationCounters.DamageRequestDedupes, InstrumentationCounters.StarvedPuppetFrames,
                InstrumentationCounters.KillLedgerIds, InstrumentationCounters.DormantDamageQueuedCount,
                InstrumentationCounters.DormantDamageReplayedCount, WorldSync.Revision, WorldSync.LedgerHash,
                WorldSync.LedgerCount, InstrumentationCounters.TerrainMismatches,
                InstrumentationCounters.TerrainRepairsSent, InstrumentationCounters.TerrainRepairsApplied,
                InstrumentationCounters.PositionSegmentDrops, InstrumentationCounters.AuthorityStateDrops,
                InstrumentationCounters.DisconnectShipDespawns, InstrumentationCounters.StationRespawnsAssigned,
                InstrumentationCounters.AuthoritySnapshotRebaselines,
                InstrumentationCounters.StarvedOwnershipRequests,
                InstrumentationCounters.StarvedOwnershipPromotions,
                InstrumentationCounters.AvailabilityCandidateDeferrals,
                InstrumentationCounters.LocalAuthorityComponentRepairs));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Residency] mono={0:0.000}s ownerNoSimSegments={1} fixedOwnedNotLive={2} " +
                "baselineOrigins=live{3}/last{4}/gen{5}/cache{6} dmgForwarded={7} claimsDropped={8} " +
                "firstSnap=ok{9}/miss{10}/max{11:0}ms handoffRejects={12} handoffMax={13:0}ms " +
                "dormantLeases={14} dormancyCommits=tx{15}/rx{16} dormantTransitions={17}(cache{18}) " +
                "residencyReports=tx{19}/rx{20}",
                mono,
                AuthorityManager.CountOwnedSegmentsNotResident(NetSession.Instance),
                EnemySync.CountFixedOwnedNotLive(NetSession.Instance),
                InstrumentationCounters.BaselineEntriesLive,
                InstrumentationCounters.BaselineEntriesLastKnown,
                InstrumentationCounters.BaselineEntriesGeneration,
                InstrumentationCounters.BaselineEntriesCache,
                InstrumentationCounters.DormantDamageForwardedCount,
                InstrumentationCounters.DormantClaimsDroppedCount,
                InstrumentationCounters.FirstSnapshotsObserved,
                InstrumentationCounters.FirstSnapshotDeadlineMisses,
                InstrumentationCounters.FirstSnapshotMaxMs,
                InstrumentationCounters.HandoffRejects,
                InstrumentationCounters.HandoffDurationMaxMs,
                AuthorityManager.DormantLeaseCount,
                InstrumentationCounters.DormancyCommitsSent,
                InstrumentationCounters.DormancyCommitsApplied,
                InstrumentationCounters.DormantTransitions,
                InstrumentationCounters.DormantTransitionsFromCache,
                InstrumentationCounters.ResidencyReportsSent,
                InstrumentationCounters.ResidencyReportsApplied));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[ProjectileTimeline] mono={0:0.000}s pending={1} queued={2} late={3} muzzleCorrectionAvg={4:0.000} max={5:0.000}",
                mono, ProjectileSync.PendingShipFireCount, ProjectileSync.ShipFireQueued,
                ProjectileSync.ShipFireLate, ProjectileSync.MuzzleCorrectionAverage,
                ProjectileSync.MuzzleCorrectionMax));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[PlantLedger] mono={0:0.000}s killedFruits={1} announced={2} applied={3} missingLiveChild={4}",
                mono, EnemySync.PlantFruitKilledCount, EnemySync.PlantFruitAnnouncedCount,
                EnemySync.PlantFruitAppliedCount, EnemySync.PlantFruitMissingCount));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[EntityMotion] mono={0:0.000}s corrections={1} avg={2:0.000} p95={9:0.000} max={3:0.000} hardSnaps={4} clockSamplesCoalesced={5} propHeartbeats={6} deathRepairs={7} deathRepairMax={8:0.000}",
                mono, InstrumentationCounters.EntityCorrectionCount,
                InstrumentationCounters.EntityCorrectionAverage,
                InstrumentationCounters.EntityCorrectionMax,
                InstrumentationCounters.EntityHardSnaps,
                InstrumentationCounters.EntityClockSamplesCoalescedCount,
                InstrumentationCounters.StationaryPropHeartbeats,
                InstrumentationCounters.DeathPositionRepairs,
                InstrumentationCounters.DeathPositionRepairMax,
                InstrumentationCounters.EntityCorrectionP95));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[SnapshotLatency] mono={0:0.000}s chunks={1}/{2} missing={3} maxBytes={4} keyframes={5} omittedFields={6} deltaNoBaseline={7} baselines=req{8}/apply{9}/ack{10}/handoff{11}/cache{12} roster=materialized{28}/missing{29}/incomplete{30} direct=tx{13}/rx{14}/relayBypassEntries{15} boundary={26}/{27} adaptiveDelayAvg={16:0.0}ms jitterAvg={17:0.0}ms underruns={18} relayAvg={19:0.00}ms relayMax={20:0.00}ms staleLifetime={21} durable={22} staleMutation={23} mutationGaps={24} unauthorized={25} visualMismatch={31}",
                mono, InstrumentationCounters.SnapshotChunksSent, InstrumentationCounters.SnapshotChunksReceived,
                InstrumentationCounters.SnapshotChunksMissingCount, InstrumentationCounters.SnapshotMaxBytes,
                InstrumentationCounters.SnapshotKeyframes, InstrumentationCounters.SnapshotDeltaFieldsOmittedCount,
                InstrumentationCounters.SnapshotDeltasWithoutBaseline, InstrumentationCounters.RuntimeBaselinesRequested,
                InstrumentationCounters.RuntimeBaselinesApplied, InstrumentationCounters.RuntimeBaselinesAcked,
                InstrumentationCounters.RuntimeHandoffBaselines, InstrumentationCounters.RuntimeBaselineCacheFallbacks,
                InstrumentationCounters.DirectSnapshotsSent, InstrumentationCounters.DirectSnapshotsReceived,
                InstrumentationCounters.DirectRelayBypassedEntries, InstrumentationCounters.AdaptiveDelayAverageMs,
                InstrumentationCounters.AdaptiveJitterAverageMs, InstrumentationCounters.InterpolationUnderruns,
                InstrumentationCounters.HostRelayAverageMs, InstrumentationCounters.HostRelayMaxMs,
                InstrumentationCounters.StaleLifetimesDropped, InstrumentationCounters.DurableMutationsApplied,
                InstrumentationCounters.StaleMutationsDropped, InstrumentationCounters.MutationRevisionGaps,
                InstrumentationCounters.UnauthorizedMutationsDropped,
                InstrumentationCounters.EntityBoundaryHandoffsSent,
                InstrumentationCounters.EntityBoundaryHandoffsApplied,
                InstrumentationCounters.RuntimeBaselineEntitiesMaterialized,
                InstrumentationCounters.RuntimeBaselineEntitiesMissing,
                InstrumentationCounters.RuntimeBaselineIncompleteCount,
                InstrumentationCounters.VisualGenerationMismatches));
        }

        private static void ReportStateFlow(double mono, double elapsed)
        {
            long txBundles = Delta(InstrumentationCounters.StateBundlesSent, ref _lastStateBundlesSent);
            long txGroups = Delta(InstrumentationCounters.StateGroupsSent, ref _lastStateGroupsSent);
            long txEntries = Delta(InstrumentationCounters.StateEntriesSent, ref _lastStateEntriesSent);
            long txBytes = Delta(InstrumentationCounters.StateBytesSent, ref _lastStateBytesSent);
            long rxBundles = Delta(InstrumentationCounters.StateBundlesReceived, ref _lastStateBundlesReceived);
            long rxGroups = Delta(InstrumentationCounters.StateGroupsReceived, ref _lastStateGroupsReceived);
            long rxEntries = Delta(InstrumentationCounters.StateEntriesReceived, ref _lastStateEntriesReceived);
            long filteredGroups = Delta(InstrumentationCounters.StateGroupsFiltered, ref _lastStateGroupsFiltered);
            long filteredEntries = Delta(InstrumentationCounters.StateEntriesFiltered, ref _lastStateEntriesFiltered);
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[StateFlow] mono={0:0.000}s txBundles={1:0.#}/s txGroups={2:0.#}/s txEntries={3:0.#}/s avgGroups={4:0.0} avgEntries={5:0.0} tx={6:0.0}KB/s rxBundles={7:0.#}/s rxGroups={8:0.#}/s rxEntries={9:0.#}/s filteredGroups={10:0.#}/s filteredEntries={11:0.#}/s",
                mono, txBundles / elapsed, txGroups / elapsed, txEntries / elapsed,
                txBundles > 0 ? txGroups / (double)txBundles : 0,
                txBundles > 0 ? txEntries / (double)txBundles : 0,
                txBytes / elapsed / 1024.0, rxBundles / elapsed, rxGroups / elapsed,
                rxEntries / elapsed, filteredGroups / elapsed, filteredEntries / elapsed));
        }

        private static void ReportPopulation(double mono, double elapsed)
        {
            EnemySync.GetPopulationAudit(out int units, out int props, PopulationTypes, PopulationSegments);
            EnemySync.GetReplacementTypes(ReplacementTypes);
            int reentries = EnemySync.ReenteredLifetimeCount;
            int overlaps = EnemySync.OverlappingLifetimeCount;
            int first = EnemySync.FirstLifetimeCount;
            int retired = EnemySync.RetiredLifetimeCount;
            double firstRate = Math.Max(0, first - _lastFirstLifetimes) / elapsed;
            double reentryRate = Math.Max(0, reentries - _lastReenteredLifetimes) / elapsed;
            double overlapRate = Math.Max(0, overlaps - _lastOverlappingLifetimes) / elapsed;
            double retiredRate = Math.Max(0, retired - _lastRetiredLifetimes) / elapsed;
            _lastFirstLifetimes = first;
            _lastReenteredLifetimes = reentries;
            _lastOverlappingLifetimes = overlaps;
            _lastRetiredLifetimes = retired;
            int active = InstrumentationCounters.ActiveDuplicateLifetimes;
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Population] mono={0:0.000}s canonical={1} units={2} props={3} unitTypes={4} unitSegments={5} lifetimes={6}/{7}/{8} lifetimeRates={9:0.0}/{10:0.0}/{11:0.0}/s quarantines={12}/{13} retired={14} retiredRate={15:0.0}/s oldest={16:0.00}s replacementTypes={17}",
                mono, units + props, units, props, Top(PopulationTypes, 10), Top(PopulationSegments, 12),
                first, reentries, overlaps, firstRate, reentryRate, overlapRate,
                active, EnemySync.PendingRetirementCount, retired, retiredRate,
                EnemySync.OldestQuarantineAge, Top(ReplacementTypes, 10)));

            bool pressure = active > 100 || overlapRate > 5.0;
            if (pressure && !_populationPressureWarned)
            {
                _populationPressureWarned = true;
                Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                    "[Population] PRESSURE activeQuarantines={0} overlapRate={1:0.0}/s; duplicate retirement or loader churn is not keeping up",
                    active, overlapRate));
            }
            else if (!pressure) _populationPressureWarned = false;
        }

        private static long Delta(long current, ref long previous)
        {
            long value = Math.Max(0, current - previous);
            previous = current;
            return value;
        }

        private static void MaybeScanObjects(double mono)
        {
            float configured = NetConfig.ProfileObjectScanInterval.Value;
            if (_slowScansDisabled || configured <= 0 || Time.unscaledTime < _nextObjectScanAt) return;
            _nextObjectScanAt = Time.unscaledTime + Math.Max(3f, configured);
            var sw = Stopwatch.StartNew();
            int projectiles = UnityEngine.Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None).Length;
            int physics = UnityEngine.Object.FindObjectsByType<PhysicsProjectile>(FindObjectsSortMode.None).Length;
            int gameObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
            var puppets = UnityEngine.Object.FindObjectsByType<RemoteEntityPuppet>(FindObjectsSortMode.None);
            var inertLifetimes = UnityEngine.Object.FindObjectsByType<DuplicateEntityInert>(FindObjectsSortMode.None);
            int never = 0, stale = 0, duplicateIds = 0, inertDuplicates = 0;
            int starvedDetails = 0;
            var starvedIdentities = new StringBuilder();
            var ids = new System.Collections.Generic.HashSet<int>();
            var types = new System.Collections.Generic.Dictionary<string, int>();
            var segments = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var puppet in puppets)
            {
                if (puppet == null) continue;
                if (puppet.GetComponent<DuplicateEntityInert>() != null) inertDuplicates++;
                if (!ids.Add(puppet.NetId)) duplicateIds++;
                bool neverReceived = !puppet.HasSnapshot;
                bool isStale = !neverReceived && puppet.SnapshotAge > 2f;
                if (neverReceived) never++; else if (isStale) stale++;
                var se = puppet.GetComponent<SavableEntity>();
                string type = se?.EntityData?.entityId ?? "<unknown>";
                types[type] = types.TryGetValue(type, out int tc) ? tc + 1 : 1;
                var pos = (Vector2)puppet.transform.position;
                var key = AuthorityManager.SegmentOf(pos).ToString();
                segments[key] = segments.TryGetValue(key, out int sc) ? sc + 1 : 1;
                if ((neverReceived || isStale) && starvedDetails++ < 8)
                {
                    if (starvedIdentities.Length > 0) starvedIdentities.Append(',');
                    starvedIdentities.Append('#').Append(puppet.NetId).Append('/').Append(type)
                        .Append('@').Append(key).Append(" owner=P").Append(EnemySync.OwnerOf(puppet.NetId) + 1)
                        .Append(" age=").Append(neverReceived ? "never" : puppet.SnapshotAge.ToString("0.0", CultureInfo.InvariantCulture));
                    EnemySync.IsStableAvailabilityCandidate(se, puppet, out float distance, out string gate);
                    starvedIdentities.Append(" dist=")
                        .Append(float.IsPositiveInfinity(distance) ? "inf" : distance.ToString("0.0", CultureInfo.InvariantCulture))
                        .Append(" gate=").Append(gate);
                }
            }
            sw.Stop();
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Counts] mono={0:0.000}s scan projectiles={1} physicsProjectiles={2} gameObjects={3} scanMs={4:0.0}",
                mono, projectiles, physics, gameObjects, sw.Elapsed.TotalMilliseconds));
            Plugin.Log.LogInfo($"[EntityAudit] mono={mono:0.000}s puppets={puppets.Length} uniqueNetIds={ids.Count} duplicateNetIds={duplicateIds} inertDuplicates={inertDuplicates} canonicalLifetimes={EnemySync.CanonicalLifetimeCount} activeDuplicateLifetimes={InstrumentationCounters.ActiveDuplicateLifetimes} unsafeDuplicateLifetimes={EnemySync.UnsafeDuplicateLifetimeCount} neverSnapshot={never} staleSnapshot={stale} starved={starvedIdentities} types={Top(types, 8)} segments={Top(segments, 8)}");
            if (inertLifetimes.Length > 0)
            {
                var duplicateTypes = new System.Collections.Generic.Dictionary<string, int>();
                var duplicateIdsByType = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var inert in inertLifetimes)
                {
                    if (inert == null) continue;
                    var se = inert.GetComponent<SavableEntity>();
                    string type = se?.EntityData?.entityId ?? "<unknown>";
                    duplicateTypes[type] = duplicateTypes.TryGetValue(type, out int count) ? count + 1 : 1;
                    string identity = $"#{inert.NetId}/{type}";
                    duplicateIdsByType[identity] = duplicateIdsByType.TryGetValue(identity, out int idCount) ? idCount + 1 : 1;
                }
                Plugin.Log.LogInfo($"[DuplicateAudit] mono={mono:0.000}s quarantined={inertLifetimes.Length} totalQuarantined={InstrumentationCounters.DuplicateLifetimesQuarantined} types={Top(duplicateTypes, 8)} identities={Top(duplicateIdsByType, 12)}");
            }
            if (sw.Elapsed.TotalMilliseconds > 20)
            {
                _slowScansDisabled = true;
                Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                    "[Counts] slow object scan {0:0.0}ms; disabled for this run", sw.Elapsed.TotalMilliseconds));
            }
        }

        private static string Top(System.Collections.Generic.Dictionary<string, int> counts, int limit)
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(limit, list.Count); i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i].Key).Append(':').Append(list[i].Value);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static readonly System.Collections.Generic.HashSet<string> Warned =
            new System.Collections.Generic.HashSet<string>();

        private static void WarnOnce(string subsystem, Exception e)
        {
            if (Warned.Add(subsystem))
                Plugin.Log.LogWarning($"[Profile] {subsystem} instrumentation failed: {e.Message}");
        }

        private static void ApplyWatchdogConfig()
        {
            MainThreadWatchdog.Configure(NetConfig.HitchWatchdog.Value,
                NetConfig.HitchThresholdMs.Value, NetConfig.HitchRepeatMs.Value,
                NetConfig.CaptureHitchStack.Value);
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    internal static class InstrumentationCounters
    {
        private static int _remoteShips;
        private static int _remoteEntities;
        private static long _cellChanges;
        private static long _visualSpawns;
        private static long _leaseCommits, _leaseAcks, _staleStateDrops, _duplicatesPrevented;
        private static long _positionSegmentDrops, _authorityStateDrops;
        private static long _disconnectShipDespawns, _stationRespawnsAssigned;
        private static long _authoritySnapshotRebaselines;
        private static long _starvedOwnershipRequests, _starvedOwnershipPromotions;
        private static long _availabilityCandidateDeferrals, _localAuthorityComponentRepairs;
        private static int _activeDuplicateLifetimes;
        private static long _duplicateLifetimesQuarantined, _duplicateFireDrops, _duplicateImpactDrops;
        private static long _staleFireDrops, _damageRequestDedupes;
        private static long _starvedPuppetFrames, _killLedgerIds;
        private static long _dormantDamageQueued, _dormantDamageReplayed;
        private static long _terrainRevisions, _terrainDigests, _terrainMismatches, _terrainRepairsSent, _terrainRepairsApplied;
        private static long _duplicateLifetimesRetired;
        private static long _stateBundlesSent, _stateGroupsSent, _stateEntriesSent, _stateBytesSent;
        private static long _stateBundlesReceived, _stateGroupsReceived, _stateEntriesReceived;
        private static long _stateGroupsFiltered, _stateEntriesFiltered;
        private static long _entityCorrectionCount, _entityCorrectionMillimeters, _entityCorrectionMaxMillimeters;
        private static readonly long[] EntityCorrectionBuckets = new long[9];
        private static readonly long[] EntityCorrectionBucketLimitsMm = { 10, 25, 50, 100, 250, 500, 1000, 4000, long.MaxValue };
        private static long _entityHardSnaps, _entityClockSamplesCoalesced, _stationaryPropHeartbeats;
        private static long _deathPositionRepairs, _deathPositionRepairMaxMillimeters;
        private static long _snapshotChunksSent, _snapshotChunksReceived, _snapshotChunksMissing, _snapshotMaxBytes;
        private static long _snapshotKeyframes, _snapshotDeltaFieldsOmitted, _snapshotDeltaWithoutBaseline;
        private static long _runtimeBaselinesRequested, _runtimeBaselinesApplied, _runtimeBaselinesAcked;
        private static long _runtimeHandoffBaselines, _runtimeBaselineCacheFallbacks;
        private static long _runtimeBaselineEntitiesMaterialized, _runtimeBaselineEntitiesMissing;
        private static long _runtimeBaselineIncomplete, _visualGenerationMismatches;
        private static long _directSnapshotsSent, _directSnapshotsReceived, _directRelayBypassedEntries;
        private static long _adaptiveSamples, _adaptiveDelayMicros, _adaptiveJitterMicros, _interpolationUnderruns;
        private static long _hostRelaySamples, _hostRelayMicros, _hostRelayMaxMicros;
        private static long _staleLifetimesDropped, _durableMutationsApplied, _staleMutationsDropped;
        private static long _mutationRevisionGaps, _unauthorizedMutationsDropped;
        private static long _entityBoundaryHandoffsSent, _entityBoundaryHandoffsApplied;
        private static long _baselineEntriesLive, _baselineEntriesLastKnown;
        private static long _baselineEntriesGeneration, _baselineEntriesCache;
        private static long _dormantDamageForwarded, _dormantClaimsDropped;
        private static long _firstSnapshotsObserved, _firstSnapshotDeadlineMisses, _firstSnapshotMaxMicros;
        private static long _handoffDurationMaxMicros, _handoffRejects;
        private static long _residencyReportsSent, _residencyReportsApplied;
        private static long _dormancyCommitsSent, _dormancyCommitsApplied;
        private static long _dormantTransitions, _dormantTransitionsFromCache;

        internal static int RemoteShips => Math.Max(0, Volatile.Read(ref _remoteShips));
        internal static int RemoteEntities => Math.Max(0, Volatile.Read(ref _remoteEntities));
        internal static long CellChanges => Interlocked.Read(ref _cellChanges);
        internal static long VisualSpawns => Interlocked.Read(ref _visualSpawns);
        internal static long LeaseCommits => Interlocked.Read(ref _leaseCommits);
        internal static long LeaseAcks => Interlocked.Read(ref _leaseAcks);
        internal static long StaleStateDrops => Interlocked.Read(ref _staleStateDrops);
        internal static long PositionSegmentDrops => Interlocked.Read(ref _positionSegmentDrops);
        internal static long AuthorityStateDrops => Interlocked.Read(ref _authorityStateDrops);
        internal static long DisconnectShipDespawns => Interlocked.Read(ref _disconnectShipDespawns);
        internal static long StationRespawnsAssigned => Interlocked.Read(ref _stationRespawnsAssigned);
        internal static long AuthoritySnapshotRebaselines => Interlocked.Read(ref _authoritySnapshotRebaselines);
        internal static long StarvedOwnershipRequests => Interlocked.Read(ref _starvedOwnershipRequests);
        internal static long StarvedOwnershipPromotions => Interlocked.Read(ref _starvedOwnershipPromotions);
        internal static long AvailabilityCandidateDeferrals => Interlocked.Read(ref _availabilityCandidateDeferrals);
        internal static long LocalAuthorityComponentRepairs => Interlocked.Read(ref _localAuthorityComponentRepairs);
        internal static long DuplicatesPrevented => Interlocked.Read(ref _duplicatesPrevented);
        internal static int ActiveDuplicateLifetimes => Math.Max(0, Volatile.Read(ref _activeDuplicateLifetimes));
        internal static long DuplicateLifetimesQuarantined => Interlocked.Read(ref _duplicateLifetimesQuarantined);
        internal static long DuplicateFireDrops => Interlocked.Read(ref _duplicateFireDrops);
        internal static long DuplicateImpactDrops => Interlocked.Read(ref _duplicateImpactDrops);
        internal static long StaleFireDrops => Interlocked.Read(ref _staleFireDrops);
        internal static long DamageRequestDedupes => Interlocked.Read(ref _damageRequestDedupes);
        internal static long StarvedPuppetFrames => Interlocked.Read(ref _starvedPuppetFrames);
        internal static long KillLedgerIds => Interlocked.Read(ref _killLedgerIds);
        internal static long DormantDamageQueuedCount => Interlocked.Read(ref _dormantDamageQueued);
        internal static long DormantDamageReplayedCount => Interlocked.Read(ref _dormantDamageReplayed);
        internal static long TerrainMismatches => Interlocked.Read(ref _terrainMismatches);
        internal static long TerrainRepairsSent => Interlocked.Read(ref _terrainRepairsSent);
        internal static long TerrainRepairsApplied => Interlocked.Read(ref _terrainRepairsApplied);
        internal static long DuplicateLifetimesRetired => Interlocked.Read(ref _duplicateLifetimesRetired);
        internal static long StateBundlesSent => Interlocked.Read(ref _stateBundlesSent);
        internal static long StateGroupsSent => Interlocked.Read(ref _stateGroupsSent);
        internal static long StateEntriesSent => Interlocked.Read(ref _stateEntriesSent);
        internal static long StateBytesSent => Interlocked.Read(ref _stateBytesSent);
        internal static long StateBundlesReceived => Interlocked.Read(ref _stateBundlesReceived);
        internal static long StateGroupsReceived => Interlocked.Read(ref _stateGroupsReceived);
        internal static long StateEntriesReceived => Interlocked.Read(ref _stateEntriesReceived);
        internal static long StateGroupsFiltered => Interlocked.Read(ref _stateGroupsFiltered);
        internal static long StateEntriesFiltered => Interlocked.Read(ref _stateEntriesFiltered);
        internal static long EntityCorrectionCount => Interlocked.Read(ref _entityCorrectionCount);
        internal static double EntityCorrectionAverage => EntityCorrectionCount > 0
            ? Interlocked.Read(ref _entityCorrectionMillimeters) / 1000.0 / EntityCorrectionCount : 0.0;
        internal static double EntityCorrectionMax => Interlocked.Read(ref _entityCorrectionMaxMillimeters) / 1000.0;
        internal static double EntityCorrectionP95
        {
            get
            {
                long total = EntityCorrectionCount;
                if (total <= 0) return 0;
                long target = (long)Math.Ceiling(total * 0.95);
                long cumulative = 0;
                for (int i = 0; i < EntityCorrectionBuckets.Length; i++)
                {
                    cumulative += Interlocked.Read(ref EntityCorrectionBuckets[i]);
                    if (cumulative >= target)
                        return EntityCorrectionBucketLimitsMm[i] == long.MaxValue
                            ? EntityCorrectionMax : EntityCorrectionBucketLimitsMm[i] / 1000.0;
                }
                return EntityCorrectionMax;
            }
        }
        internal static long EntityHardSnaps => Interlocked.Read(ref _entityHardSnaps);
        internal static long EntityClockSamplesCoalescedCount => Interlocked.Read(ref _entityClockSamplesCoalesced);
        internal static long StationaryPropHeartbeats => Interlocked.Read(ref _stationaryPropHeartbeats);
        internal static long DeathPositionRepairs => Interlocked.Read(ref _deathPositionRepairs);
        internal static double DeathPositionRepairMax => Interlocked.Read(ref _deathPositionRepairMaxMillimeters) / 1000.0;
        internal static long SnapshotChunksSent => Interlocked.Read(ref _snapshotChunksSent);
        internal static long SnapshotChunksReceived => Interlocked.Read(ref _snapshotChunksReceived);
        internal static long SnapshotChunksMissingCount => Interlocked.Read(ref _snapshotChunksMissing);
        internal static long SnapshotMaxBytes => Interlocked.Read(ref _snapshotMaxBytes);
        internal static long SnapshotKeyframes => Interlocked.Read(ref _snapshotKeyframes);
        internal static long SnapshotDeltaFieldsOmittedCount => Interlocked.Read(ref _snapshotDeltaFieldsOmitted);
        internal static long SnapshotDeltasWithoutBaseline => Interlocked.Read(ref _snapshotDeltaWithoutBaseline);
        internal static long RuntimeBaselinesRequested => Interlocked.Read(ref _runtimeBaselinesRequested);
        internal static long RuntimeBaselinesApplied => Interlocked.Read(ref _runtimeBaselinesApplied);
        internal static long RuntimeBaselinesAcked => Interlocked.Read(ref _runtimeBaselinesAcked);
        internal static long RuntimeHandoffBaselines => Interlocked.Read(ref _runtimeHandoffBaselines);
        internal static long RuntimeBaselineCacheFallbacks => Interlocked.Read(ref _runtimeBaselineCacheFallbacks);
        internal static long RuntimeBaselineEntitiesMaterialized => Interlocked.Read(ref _runtimeBaselineEntitiesMaterialized);
        internal static long RuntimeBaselineEntitiesMissing => Interlocked.Read(ref _runtimeBaselineEntitiesMissing);
        internal static long RuntimeBaselineIncompleteCount => Interlocked.Read(ref _runtimeBaselineIncomplete);
        internal static long VisualGenerationMismatches => Interlocked.Read(ref _visualGenerationMismatches);
        internal static long DirectSnapshotsSent => Interlocked.Read(ref _directSnapshotsSent);
        internal static long DirectSnapshotsReceived => Interlocked.Read(ref _directSnapshotsReceived);
        internal static long DirectRelayBypassedEntries => Interlocked.Read(ref _directRelayBypassedEntries);
        internal static long InterpolationUnderruns => Interlocked.Read(ref _interpolationUnderruns);
        internal static double AdaptiveDelayAverageMs => Interlocked.Read(ref _adaptiveSamples) > 0
            ? Interlocked.Read(ref _adaptiveDelayMicros) / 1000.0 / Interlocked.Read(ref _adaptiveSamples) : 0;
        internal static double AdaptiveJitterAverageMs => Interlocked.Read(ref _adaptiveSamples) > 0
            ? Interlocked.Read(ref _adaptiveJitterMicros) / 1000.0 / Interlocked.Read(ref _adaptiveSamples) : 0;
        internal static double HostRelayAverageMs => Interlocked.Read(ref _hostRelaySamples) > 0
            ? Interlocked.Read(ref _hostRelayMicros) / 1000.0 / Interlocked.Read(ref _hostRelaySamples) : 0;
        internal static double HostRelayMaxMs => Interlocked.Read(ref _hostRelayMaxMicros) / 1000.0;
        internal static long StaleLifetimesDropped => Interlocked.Read(ref _staleLifetimesDropped);
        internal static long DurableMutationsApplied => Interlocked.Read(ref _durableMutationsApplied);
        internal static long StaleMutationsDropped => Interlocked.Read(ref _staleMutationsDropped);
        internal static long MutationRevisionGaps => Interlocked.Read(ref _mutationRevisionGaps);
        internal static long UnauthorizedMutationsDropped => Interlocked.Read(ref _unauthorizedMutationsDropped);
        internal static long EntityBoundaryHandoffsSent => Interlocked.Read(ref _entityBoundaryHandoffsSent);
        internal static long EntityBoundaryHandoffsApplied => Interlocked.Read(ref _entityBoundaryHandoffsApplied);
        internal static void RemoteShipAdded() => Interlocked.Increment(ref _remoteShips);
        internal static void RemoteShipRemoved() => Interlocked.Decrement(ref _remoteShips);
        internal static void RemoteEntityAdded() => Interlocked.Increment(ref _remoteEntities);
        internal static void RemoteEntityRemoved() => Interlocked.Decrement(ref _remoteEntities);
        internal static void CellCaptured() => Interlocked.Increment(ref _cellChanges);
        internal static void VisualProjectileSpawned() => Interlocked.Increment(ref _visualSpawns);
        internal static void LeaseCommitted() => Interlocked.Increment(ref _leaseCommits);
        internal static void LeaseAcked() => Interlocked.Increment(ref _leaseAcks);
        internal static void StaleEntityStateDropped() => Interlocked.Increment(ref _staleStateDrops);
        internal static void PositionSegmentStateDropped()
        {
            Interlocked.Increment(ref _staleStateDrops);
            Interlocked.Increment(ref _positionSegmentDrops);
        }
        internal static void AuthorityStateDropped()
        {
            Interlocked.Increment(ref _staleStateDrops);
            Interlocked.Increment(ref _authorityStateDrops);
        }
        internal static void DisconnectShipDespawned() => Interlocked.Increment(ref _disconnectShipDespawns);
        internal static void StationRespawnAssigned() => Interlocked.Increment(ref _stationRespawnsAssigned);
        internal static void AuthoritySnapshotRebaselined() => Interlocked.Increment(ref _authoritySnapshotRebaselines);
        internal static void StarvedOwnershipRequested() => Interlocked.Increment(ref _starvedOwnershipRequests);
        internal static void StarvedOwnershipPromoted() => Interlocked.Increment(ref _starvedOwnershipPromotions);
        internal static void AvailabilityCandidateDeferred() => Interlocked.Increment(ref _availabilityCandidateDeferrals);
        internal static void LocalAuthorityComponentRepaired() => Interlocked.Increment(ref _localAuthorityComponentRepairs);
        internal static void DuplicateEntityPrevented() => Interlocked.Increment(ref _duplicatesPrevented);
        internal static void DuplicateLifetimeActivated() => Interlocked.Increment(ref _activeDuplicateLifetimes);
        internal static void DuplicateLifetimeReleased() => Interlocked.Decrement(ref _activeDuplicateLifetimes);
        internal static void DuplicateLifetimeQuarantined() => Interlocked.Increment(ref _duplicateLifetimesQuarantined);
        internal static void DuplicateLifetimeRetired() => Interlocked.Increment(ref _duplicateLifetimesRetired);
        internal static void DuplicateFireDropped() => Interlocked.Increment(ref _duplicateFireDrops);
        internal static void DuplicateImpactDropped() => Interlocked.Increment(ref _duplicateImpactDrops);
        internal static void StaleFireDropped() => Interlocked.Increment(ref _staleFireDrops);
        internal static void DamageRequestDeduped() => Interlocked.Increment(ref _damageRequestDedupes);
        internal static void StarvedPuppetFrame() => Interlocked.Increment(ref _starvedPuppetFrames);
        internal static void KillLedgerSent(int count) => Interlocked.Add(ref _killLedgerIds, count);
        internal static void DormantDamageQueued() => Interlocked.Increment(ref _dormantDamageQueued);
        internal static void DormantDamageReplayed() => Interlocked.Increment(ref _dormantDamageReplayed);
        internal static void TerrainRevisionCommitted() => Interlocked.Increment(ref _terrainRevisions);
        internal static void TerrainDigestSent() => Interlocked.Increment(ref _terrainDigests);
        internal static void TerrainMismatch() => Interlocked.Increment(ref _terrainMismatches);
        internal static void TerrainRepairSent() => Interlocked.Increment(ref _terrainRepairsSent);
        internal static void TerrainRepairApplied() => Interlocked.Increment(ref _terrainRepairsApplied);
        internal static void StateBundleSent(int groups, int entries, int bytes, int filteredGroups, int filteredEntries)
        {
            Interlocked.Increment(ref _stateBundlesSent);
            Interlocked.Add(ref _stateGroupsSent, groups);
            Interlocked.Add(ref _stateEntriesSent, entries);
            Interlocked.Add(ref _stateBytesSent, bytes);
            StateInterestFiltered(filteredGroups, filteredEntries);
        }
        internal static void StateBundleReceived(int groups, int entries)
        {
            Interlocked.Increment(ref _stateBundlesReceived);
            Interlocked.Add(ref _stateGroupsReceived, groups);
            Interlocked.Add(ref _stateEntriesReceived, entries);
        }
        internal static void StateInterestFiltered(int groups, int entries)
        {
            Interlocked.Add(ref _stateGroupsFiltered, groups);
            Interlocked.Add(ref _stateEntriesFiltered, entries);
        }
        internal static void EntitySnapshotCorrection(float worldUnits)
        {
            if (float.IsNaN(worldUnits) || float.IsInfinity(worldUnits) || worldUnits < 0f) return;
            long millimeters = (long)Math.Min(int.MaxValue, Math.Round(worldUnits * 1000.0));
            Interlocked.Increment(ref _entityCorrectionCount);
            Interlocked.Add(ref _entityCorrectionMillimeters, millimeters);
            for (int i = 0; i < EntityCorrectionBucketLimitsMm.Length; i++)
                if (millimeters <= EntityCorrectionBucketLimitsMm[i])
                {
                    Interlocked.Increment(ref EntityCorrectionBuckets[i]);
                    break;
                }
            long observed;
            while (millimeters > (observed = Interlocked.Read(ref _entityCorrectionMaxMillimeters)))
                if (Interlocked.CompareExchange(ref _entityCorrectionMaxMillimeters, millimeters, observed) == observed)
                    break;
        }
        internal static void EntityHardSnap() => Interlocked.Increment(ref _entityHardSnaps);
        internal static void EntityClockSamplesCoalesced(int count) =>
            Interlocked.Add(ref _entityClockSamplesCoalesced, Math.Max(0, count));
        internal static void StationaryPropHeartbeatSent() => Interlocked.Increment(ref _stationaryPropHeartbeats);
        internal static void DeathPositionRepaired(float worldUnits)
        {
            Interlocked.Increment(ref _deathPositionRepairs);
            long millimeters = (long)Math.Min(int.MaxValue, Math.Round(Math.Max(0f, worldUnits) * 1000.0));
            long observed;
            while (millimeters > (observed = Interlocked.Read(ref _deathPositionRepairMaxMillimeters)))
                if (Interlocked.CompareExchange(ref _deathPositionRepairMaxMillimeters, millimeters, observed) == observed)
                    break;
        }
        internal static void SnapshotChunkSent(int bytes, int chunkCount)
        {
            Interlocked.Increment(ref _snapshotChunksSent);
            UpdateMax(ref _snapshotMaxBytes, Math.Max(0, bytes));
        }
        internal static void SnapshotChunkReceived(int chunkIndex, int chunkCount) =>
            Interlocked.Increment(ref _snapshotChunksReceived);
        internal static void SnapshotChunksMissing(int count) =>
            Interlocked.Add(ref _snapshotChunksMissing, Math.Max(0, count));
        internal static void SnapshotKeyframeSent() => Interlocked.Increment(ref _snapshotKeyframes);
        internal static void SnapshotDeltaFieldsOmitted(int count) =>
            Interlocked.Add(ref _snapshotDeltaFieldsOmitted, Math.Max(0, count));
        internal static void SnapshotDeltaWithoutBaseline() => Interlocked.Increment(ref _snapshotDeltaWithoutBaseline);
        internal static void RuntimeBaselineRequested(bool handoff)
        {
            Interlocked.Increment(ref _runtimeBaselinesRequested);
            if (handoff) Interlocked.Increment(ref _runtimeHandoffBaselines);
        }
        internal static void RuntimeBaselineApplied(int entries, bool handoff) =>
            Interlocked.Increment(ref _runtimeBaselinesApplied);
        internal static void RuntimeBaselineAcked(bool handoff) => Interlocked.Increment(ref _runtimeBaselinesAcked);
        internal static void RuntimeBaselineCacheFallback(int entries) =>
            Interlocked.Increment(ref _runtimeBaselineCacheFallbacks);
        internal static void RuntimeBaselineEntityMaterialized() =>
            Interlocked.Increment(ref _runtimeBaselineEntitiesMaterialized);
        internal static void RuntimeBaselineEntityMissing() =>
            Interlocked.Increment(ref _runtimeBaselineEntitiesMissing);
        internal static void RuntimeBaselineIncomplete() => Interlocked.Increment(ref _runtimeBaselineIncomplete);
        internal static void VisualGenerationMismatch() => Interlocked.Increment(ref _visualGenerationMismatches);
        internal static void DirectSnapshotSent(int entries) => Interlocked.Add(ref _directSnapshotsSent, Math.Max(0, entries));
        internal static void DirectSnapshotReceived(int entries) => Interlocked.Add(ref _directSnapshotsReceived, Math.Max(0, entries));
        internal static void DirectRelayBypassed(int entries) => Interlocked.Add(ref _directRelayBypassedEntries, Math.Max(0, entries));
        internal static void AdaptiveTimingSample(float delaySeconds, float jitterSeconds)
        {
            Interlocked.Increment(ref _adaptiveSamples);
            Interlocked.Add(ref _adaptiveDelayMicros, (long)(Math.Max(0f, delaySeconds) * 1000000f));
            Interlocked.Add(ref _adaptiveJitterMicros, (long)(Math.Max(0f, jitterSeconds) * 1000000f));
        }
        internal static void InterpolationUnderrun() => Interlocked.Increment(ref _interpolationUnderruns);
        internal static void HostRelayCompleted(float milliseconds, int bytes)
        {
            long micros = (long)(Math.Max(0f, milliseconds) * 1000f);
            Interlocked.Increment(ref _hostRelaySamples);
            Interlocked.Add(ref _hostRelayMicros, micros);
            UpdateMax(ref _hostRelayMaxMicros, micros);
        }
        internal static void StaleLifetimeDropped() => Interlocked.Increment(ref _staleLifetimesDropped);
        internal static void DurableMutationApplied() => Interlocked.Increment(ref _durableMutationsApplied);
        internal static void StaleMutationDropped() => Interlocked.Increment(ref _staleMutationsDropped);
        internal static void MutationRevisionGap(uint count) => Interlocked.Add(ref _mutationRevisionGaps, count);
        internal static void UnauthorizedMutationDropped() => Interlocked.Increment(ref _unauthorizedMutationsDropped);
        internal static void EntityBoundaryHandoffSent() => Interlocked.Increment(ref _entityBoundaryHandoffsSent);
        internal static void EntityBoundaryHandoffApplied() => Interlocked.Increment(ref _entityBoundaryHandoffsApplied);

        internal static long BaselineEntriesLive => Interlocked.Read(ref _baselineEntriesLive);
        internal static long BaselineEntriesLastKnown => Interlocked.Read(ref _baselineEntriesLastKnown);
        internal static long BaselineEntriesGeneration => Interlocked.Read(ref _baselineEntriesGeneration);
        internal static long BaselineEntriesCache => Interlocked.Read(ref _baselineEntriesCache);
        internal static long DormantDamageForwardedCount => Interlocked.Read(ref _dormantDamageForwarded);
        internal static long DormantClaimsDroppedCount => Interlocked.Read(ref _dormantClaimsDropped);
        internal static long FirstSnapshotsObserved => Interlocked.Read(ref _firstSnapshotsObserved);
        internal static long FirstSnapshotDeadlineMisses => Interlocked.Read(ref _firstSnapshotDeadlineMisses);
        internal static double FirstSnapshotMaxMs => Interlocked.Read(ref _firstSnapshotMaxMicros) / 1000.0;
        internal static double HandoffDurationMaxMs => Interlocked.Read(ref _handoffDurationMaxMicros) / 1000.0;
        internal static long HandoffRejects => Interlocked.Read(ref _handoffRejects);

        internal static void BaselineEntryBuilt(Protocol.BaselineEntryOrigin origin)
        {
            switch (origin)
            {
                case Protocol.BaselineEntryOrigin.Live: Interlocked.Increment(ref _baselineEntriesLive); break;
                case Protocol.BaselineEntryOrigin.LastKnown: Interlocked.Increment(ref _baselineEntriesLastKnown); break;
                case Protocol.BaselineEntryOrigin.Generation: Interlocked.Increment(ref _baselineEntriesGeneration); break;
                default: Interlocked.Increment(ref _baselineEntriesCache); break;
            }
        }
        internal static void DormantDamageForwarded() => Interlocked.Increment(ref _dormantDamageForwarded);
        internal static void DormantClaimDropped() => Interlocked.Increment(ref _dormantClaimsDropped);
        internal static void FirstSnapshotObserved(float seconds)
        {
            Interlocked.Increment(ref _firstSnapshotsObserved);
            UpdateMax(ref _firstSnapshotMaxMicros, (long)(Math.Max(0f, seconds) * 1000000f));
        }
        internal static void FirstSnapshotDeadlineMissed() => Interlocked.Increment(ref _firstSnapshotDeadlineMisses);
        internal static void HandoffCommitted(float seconds) =>
            UpdateMax(ref _handoffDurationMaxMicros, (long)(Math.Max(0f, seconds) * 1000000f));
        internal static void HandoffRejected() => Interlocked.Increment(ref _handoffRejects);

        internal static long ResidencyReportsSent => Interlocked.Read(ref _residencyReportsSent);
        internal static long ResidencyReportsApplied => Interlocked.Read(ref _residencyReportsApplied);
        internal static long DormancyCommitsSent => Interlocked.Read(ref _dormancyCommitsSent);
        internal static long DormancyCommitsApplied => Interlocked.Read(ref _dormancyCommitsApplied);
        internal static long DormantTransitions => Interlocked.Read(ref _dormantTransitions);
        internal static long DormantTransitionsFromCache => Interlocked.Read(ref _dormantTransitionsFromCache);

        internal static void ResidencyReportSent(int segments) => Interlocked.Increment(ref _residencyReportsSent);
        internal static void ResidencyReportApplied(int segments) => Interlocked.Increment(ref _residencyReportsApplied);
        internal static void DormancyCommitSent(int entries) => Interlocked.Increment(ref _dormancyCommitsSent);
        internal static void DormancyCommitApplied(int entries) => Interlocked.Increment(ref _dormancyCommitsApplied);
        internal static void DormantTransition(bool fromCache)
        {
            Interlocked.Increment(ref _dormantTransitions);
            if (fromCache) Interlocked.Increment(ref _dormantTransitionsFromCache);
        }

        private static void UpdateMax(ref long location, long value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref location)))
                if (Interlocked.CompareExchange(ref location, value, observed) == observed) break;
        }
    }
}
