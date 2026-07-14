using System;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Keeps procedural generation isolated from both Unity's process-global RNG and the game's
    /// parameterless Rnd streams.  Every scope restores the caller's streams, so generation does
    /// not perturb combat/cosmetic randomness and is repeatable on every peer.
    /// </summary>
    internal static class DeterministicGeneration
    {
        internal sealed class Scope
        {
            internal bool Active;
            internal UnityEngine.Random.State UnityState;
            internal int PreviousSeed;
            internal int PreviousCounter;
            internal int PreviousDepth;
            internal FieldInfo DistributionField;
            internal Rnd PreviousDistributionRnd;
        }

        private static int _seed;
        private static int _counter;
        private static int _depth;

        internal static int Mix(int seed, int a, int b = 0, int c = 0)
        {
            unchecked
            {
                uint h = (uint)seed ^ 2166136261u;
                h = (h ^ (uint)a) * 16777619u;
                h = (h ^ (uint)b) * 16777619u;
                h = (h ^ (uint)c) * 16777619u;
                h ^= h >> 16;
                h *= 0x7feb352du;
                h ^= h >> 15;
                return (int)(h == 0 ? 1u : h);
            }
        }

        internal static Scope Begin(int seed, Type distributionType = null)
        {
            var session = NetSession.Instance;
            var state = new Scope();
            if (!NetSession.Active || session == null || session.CurrentRunSeed == 0) return state;

            state.Active = true;
            state.UnityState = UnityEngine.Random.state;
            state.PreviousSeed = _seed;
            state.PreviousCounter = _counter;
            state.PreviousDepth = _depth;
            _seed = seed;
            _counter = 0;
            _depth++;
            UnityEngine.Random.InitState(seed);

            // Distribution.Draw() owns a static, process-seeded Rnd per closed generic type.
            // Temporarily replace only the distribution used by this generation path.
            if (distributionType != null)
            {
                for (var t = distributionType; t != null; t = t.BaseType)
                {
                    var field = t.GetField("rnd", BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (field == null || field.FieldType != typeof(Rnd)) continue;
                    state.DistributionField = field;
                    state.PreviousDistributionRnd = field.GetValue(null) as Rnd;
                    field.SetValue(null, new Rnd(Mix(seed, 0x44524157)));
                    break;
                }
            }
            return state;
        }

        internal static void End(Scope state)
        {
            if (state == null || !state.Active) return;
            if (state.DistributionField != null)
                state.DistributionField.SetValue(null, state.PreviousDistributionRnd);
            UnityEngine.Random.state = state.UnityState;
            _seed = state.PreviousSeed;
            _counter = state.PreviousCounter;
            _depth = state.PreviousDepth;
            state.Active = false;
        }

        [HarmonyPatch]
        private static class SeedParameterlessRnd
        {
            private static MethodBase TargetMethod() => AccessTools.Constructor(typeof(Rnd), Type.EmptyTypes);
            private static void Postfix(Rnd __instance)
            {
                if (_depth > 0) __instance.ChangeSeed(Mix(_seed, ++_counter, 0x524E44));
            }
        }

        [HarmonyPatch]
        private static class PlantDataGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(EntityPlant.Data), "Generate");
            private static void Prefix(EntityData __1, EntityPlantData __2, out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                int plantId = __2 != null ? __2.id : 0;
                __state = Begin(Mix(runSeed, __1?.instanceId ?? 0, plantId, 0x504C414E), typeof(IntDistribution));
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        private static class RandomObjectGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("RandomObjectGenerator"), "Generate");
            private static void Prefix(out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                __state = Begin(Mix(runSeed, 0x524F424A));
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        internal static class MergedCellGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("MergedCellsGenerator"), "Generate");
            private static void Prefix(out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                var dist = AccessTools.TypeByName("MergedCellsGenerator+MergedCellDistribution");
                __state = Begin(Mix(runSeed, 0x4D43454C), dist);
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        private static class StationConnectionGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(StationConnection), "SetPositions");
            private static void Prefix(Station.Data __0, Station.Data __1, out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                int a = __0?.entity?.instanceId ?? 0;
                int b = __1?.entity?.instanceId ?? 0;
                if (a > b) { int swap = a; a = b; b = swap; }
                __state = Begin(Mix(runSeed, a, b, 0x5354434E));
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        private static class AutoPopGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("AutoPopper"), "RegisterToPopIfNeeded");
            private static void Prefix(Vector2Int __0, int __2, out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                __state = Begin(Mix(runSeed, __0.x, __0.y, __2 ^ 0x41504F50));
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        private static class CellRegrowGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("CellRegrower"), "OnCellChanged");
            private static void Prefix(Level.CellChange __0, out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                __state = Begin(Mix(runSeed, __0.position.x, __0.position.y,
                    __0.changeSource ^ 0x52454752));
            }
            private static void Postfix(Scope __state) => End(__state);
        }

        [HarmonyPatch]
        private static class TileVisualGeneration
        {
            private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.TypeByName("UnityTilemapRenderer"), "OnLevelGenerated");
            private static void Prefix(out Scope __state)
            {
                int runSeed = NetSession.Instance?.CurrentRunSeed ?? 0;
                __state = Begin(Mix(runSeed, 0x54494C45));
            }
            private static void Postfix(Scope __state) => End(__state);
        }
    }
}
