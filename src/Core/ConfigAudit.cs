using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using BepConfigFile = BepInEx.Configuration.ConfigFile;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Startup report on what this process is actually configured to do, and a warning for every
    /// setting in config.cfg that is doing nothing.
    ///
    /// This exists because a config file is the one part of the mod that OUTLIVES the code. A
    /// dedicated server writes config.cfg once and keeps it forever, so a key we retire keeps
    /// sitting there looking authoritative — and the operator who set it has every reason to
    /// believe it still works. That is not hypothetical: the Battle Royale ring schedule was
    /// tuned three times through knobs (BrRingHoldMinutes, BrRingCloseSeconds) that a later
    /// redesign stopped reading, and nothing anywhere said so. The same silence covers a plain
    /// typo — `BrMatchMinuts = 20` parses, saves, and is ignored forever.
    ///
    /// BepInEx already does the hard half. Its ConfigFile reads the whole file up front and holds
    /// every key nobody claimed in a private OrphanedEntries dictionary, deleting each one as a
    /// plugin Binds it. So after <see cref="NetConfig.Init"/> has bound everything, whatever is
    /// left in there is EXACTLY the set of settings on disk that no longer mean anything — no file
    /// parsing of our own, and no chance of the two disagreeing.
    /// </summary>
    internal static class ConfigAudit
    {
        /// <summary>A setting we used to have, why it went, and what replaced it.
        ///
        /// Keyed "Section.Key". Retired entries are DELETED from config.cfg — we know for certain
        /// they do nothing, and leaving them behind is what created the problem. Unknown keys are
        /// left alone: an unrecognised key is usually a typo, and silently deleting it would throw
        /// away the value the operator was trying to set along with the evidence of the mistake.
        ///
        /// Add to this whenever a key is removed. An entry costs one line and turns a silent
        /// behaviour change into a sentence the operator reads on the next boot.</summary>
        private static readonly Dictionary<string, string> Retired =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Session.BrRingStartMinutes"] =
                "the ring's opening grace is now the first zone's own wait, taken from the " +
                "schedule curve — set BrMatchMinutes instead",
            ["Session.BrRingHoldMinutes"] =
                "there is no single hold any more; every zone waits for its own length (long " +
                "early, near zero late), derived from BrMatchMinutes + BrRingStages",
            ["Session.BrRingCloseSeconds"] =
                "closure time now varies per zone, derived from BrMatchMinutes + BrRingStages",
            ["Session.BrRingFirstHoldMinutes"] =
                "the first zone is no longer a special case — it is stage 1 of the schedule curve",
            ["Session.BrCarePackagesPerWave"] =
                "renamed to BrCarePackageCount, which added 0 = 'scale the wave to the player " +
                "count' as its default",
            ["Authority.AuthorityRadius"] =
                "authority is host-granted and sticky, not distance-scored; TransferRadius and " +
                "InterestRadius are the live distance knobs",
            ["Session.BrRingPaintMs"] = PaintGone,
            ["Session.BrRingPaintBudget"] = PaintGone,
            ["Sync.EntityStateHz"] =
                "renamed to StateHz when ships and entities were unified onto one snapshot rate",
            ["Diag.LogUploadEndpoint"] = "renamed to LogUploadUrl",
            ["Diag.LogUploadBase"] =
                "the log pipeline moved to presigned URLs; LogUploadUrl is the only endpoint knob",
            ["Diag.LogWebhookUrl"] =
                "the Discord webhook path was dropped when log upload went S3-only",
        };

        private const string PaintGone =
            "the ring is a rendered zone plus a radius check, not painted terrain — there is no " +
            "paint budget to spend";

        /// <summary>Never print the value of a key whose name looks like a credential. These logs
        /// are uploaded to S3 by the `uploadlogs` devcmd, so a startup dump is a publishing
        /// decision, not just a print.</summary>
        private static bool IsSecret(string key) =>
            key.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0;

        public static void Run(BepConfigFile cfg)
        {
            if (cfg == null) return;
            try { RunCore(cfg); }
            // A config report must never be the reason the mod fails to load. If BepInEx renames
            // the private dictionary out from under us, we lose the warnings and nothing else.
            catch (Exception e) { Plugin.Log.LogWarning($"[Config] audit skipped: {e.Message}"); }
        }

        private static void RunCore(BepConfigFile cfg)
        {
            var bound = cfg.GetConfigEntries();
            ReportState(bound);
            ReportDead(cfg, bound);
        }

        /// <summary>What this process is actually going to do — the settings that DIFFER from the
        /// defaults, which is the short list that explains any given run. Printing all ninety-odd
        /// would bury it.</summary>
        private static void ReportState(ConfigEntryBase[] bound)
        {
            var changed = new List<string>();
            foreach (var e in bound)
            {
                if (e == null || Equals(e.BoxedValue, e.DefaultValue)) continue;
                string key = e.Definition.Key;
                changed.Add(IsSecret(key) ? $"{key}=(set)" : $"{key}={e.GetSerializedValue()}");
            }
            changed.Sort(StringComparer.OrdinalIgnoreCase);

            Plugin.Log.LogInfo($"[Config] PunkMultiverse {Plugin.Version} — {bound.Length} settings, " +
                $"{changed.Count} changed from defaults");
            // One per line rather than a joined blob: these end up in a log someone greps, and a
            // 400-character line is not greppable.
            foreach (var c in changed) Plugin.Log.LogInfo($"[Config]   {c}");
        }

        /// <summary>Everything on disk that no longer does anything. Retired keys are named,
        /// explained, and removed; anything else is a probable typo and is kept but nagged
        /// about — including a best guess at what was meant.</summary>
        private static void ReportDead(BepConfigFile cfg, ConfigEntryBase[] bound)
        {
            var orphans = Traverse.Create(cfg).Property("OrphanedEntries")
                .GetValue<Dictionary<ConfigDefinition, string>>();
            if (orphans == null)
            {
                Plugin.Log.LogWarning("[Config] cannot read BepInEx's orphaned-entry table — " +
                    "retired and misspelled settings will not be reported this run");
                return;
            }
            if (orphans.Count == 0) return;

            // Snapshot: removing retired keys mutates the live dictionary we are walking.
            var dead = orphans.Keys.ToList();
            var knownKeys = bound.Select(e => e.Definition.Key).ToList();
            int removed = 0;

            foreach (var def in dead)
            {
                string full = $"{def.Section}.{def.Key}";
                string raw = orphans.TryGetValue(def, out var v) ? v : "";
                string shown = IsSecret(def.Key) ? "(set)" : $"'{raw}'";

                if (Retired.TryGetValue(full, out string why))
                {
                    Plugin.Log.LogWarning($"[Config] RETIRED {full} = {shown} — this setting no " +
                        $"longer does anything and has been removed from config.cfg. Reason: {why}.");
                    orphans.Remove(def);
                    removed++;
                    continue;
                }

                // The nastiest case, and the one a spelling guess describes badly: the KEY is real
                // but it is filed under the wrong [Section]. The setting then exists twice in the
                // file — this dead copy holding the value someone meant, and the live one quietly
                // holding something else. Found on the first run of this audit: a dev config had
                // Diag.SummaryHeal = true sitting above Sync.SummaryHeal = false.
                var moved = bound.FirstOrDefault(e =>
                    string.Equals(e.Definition.Key, def.Key, StringComparison.OrdinalIgnoreCase));
                if (moved != null)
                {
                    Plugin.Log.LogWarning($"[Config] WRONG SECTION {full} = {shown} — this setting " +
                        $"lives under [{moved.Definition.Section}], and the copy that is actually " +
                        $"being used reads '{moved.GetSerializedValue()}'. The line above is IGNORED. " +
                        "Move the value up to the real one and delete this line.");
                    continue;
                }

                string guess = NearestKey(def.Key, knownKeys);
                Plugin.Log.LogWarning($"[Config] UNKNOWN {full} = {shown} — not a setting this mod " +
                    "has, so it is being IGNORED" +
                    (guess != null ? $". Did you mean '{guess}'?" : ". Check the spelling.") +
                    " Left in config.cfg untouched.");
            }

            if (removed > 0)
            {
                cfg.Save();   // OrphanedEntries are written back out on save, so this is the delete
                Plugin.Log.LogInfo($"[Config] removed {removed} retired setting(s) from config.cfg");
            }
        }

        /// <summary>Closest bound key to a misspelling, or null if nothing is close enough to be
        /// worth suggesting. The threshold scales with length so short keys don't match everything.</summary>
        private static string NearestKey(string typo, List<string> known)
        {
            int best = int.MaxValue;
            string bestKey = null;
            foreach (var k in known)
            {
                int d = Distance(typo, k);
                if (d < best) { best = d; bestKey = k; }
            }
            return best <= Math.Max(2, typo.Length / 4) ? bestKey : null;
        }

        /// <summary>Levenshtein distance, case-insensitive. Two rows rather than a full matrix —
        /// this runs once per unrecognised key against ~90 candidates, but there is no reason to
        /// allocate a square.</summary>
        private static int Distance(string a, string b)
        {
            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int sub = prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), sub);
                }
                var swap = prev; prev = cur; cur = swap;
            }
            return prev[b.Length];
        }
    }
}
