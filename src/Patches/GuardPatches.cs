using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Net runs must not pollute single-player systems: no leaderboard submissions (modded,
    /// multi-pilot runs) and no suspend-saves (v1 has no save-based resume — live-session rejoin
    /// covers reconnects; a half-written net save would load as a broken solo run).
    /// </summary>
    internal static class GuardPatches
    {
        [HarmonyPatch]
        internal static class NoLeaderboardUploads
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("LeaderboardScoreSubmitter");
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        if (m.Name == "UploadScore")
                            yield return m;
            }

            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                Plugin.Log.LogInfo("[Guard] leaderboard upload blocked (net run)");
                return false;
            }
        }

        // GameController.Restart is the single chokepoint for game-over retry, game-won
        // restart, and pause-menu restart. A vanilla restart would regenerate a world past the
        // netcode's gates — in a net run the HOST's restart becomes a synchronized retry for
        // the whole party, and clients are told to wait.
        [HarmonyPatch(typeof(GameController), "Restart")]
        internal static class NetRunRestart
        {
            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                var session = NetSession.Instance;
                if (session.IsHost)
                {
                    session.RestartRun();
                    return false;
                }
                UI.Toast.Show("ONLY THE HOST CAN RETRY — WAITING FOR THE HOST", 5f);
                return false;
            }
        }

        // Returning to the main menu means LEAVING: every vanilla route there (game-over MAIN
        // MENU, pause EXIT, anything else that calls MainMenuScene.Load) now disconnects from the
        // session cleanly — the server frees the roster slot immediately instead of keeping a
        // menu-idling ghost that (being un-ready) would block the lobby's next START. The one
        // deliberate exception is the game-over BACK TO LOBBY button, which opts out for a single
        // load because staying connected is its entire point. No-ops when no session is live, so
        // boot-time and single-player menu loads are untouched.
        [HarmonyPatch(typeof(MainMenuScene), nameof(MainMenuScene.Load))]
        internal static class MenuLoadLeavesSession
        {
            /// <summary>One-shot opt-out (BACK TO LOBBY): keep the session across this load.</summary>
            internal static bool KeepSessionOnce;

            private static void Prefix()
            {
                if (KeepSessionOnce) { KeepSessionOnce = false; return; }
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active) return;
                Plugin.Log.LogInfo("[Session] main menu loaded — leaving the session");
                session.StopSession("returned to the main menu");
            }
        }

        // Restart is a synchronized full-party retry — it only makes sense after everyone has
        // died, so it lives on the GAME-OVER screen, and even there only the host's does anything.
        // Hidden for clients on game-over; removed for EVERYONE on the in-run pause menu. Hiding a
        // button would leave a hole in the fixed-position button column, so the remaining buttons
        // are compacted up into the freed slots (LayoutMenuColumn).
        [HarmonyPatch(typeof(GameOverScreen), "OnGameOver")]
        internal static class NetRunGameOverButtons
        {
            private static void Postfix(GameOverScreen __instance)
            {
                try
                {
                    if (!NetSession.Active || NetSession.Instance.IsHost) return; // host/solo: default screen
                    // Clients can't retry (that's the host's synchronized call) — but hiding the
                    // button left a lone MAIN MENU (field report 2026-07-25). Give the slot a
                    // useful role instead: BACK TO LOBBY returns to the still-live session's
                    // lobby (the session survives the menu-scene load; the wipe already put its
                    // state in Lobby) with the lobby window auto-opened, ready for the next run.
                    foreach (var button in __instance.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                    {
                        if (!button.gameObject.activeInHierarchy) continue;
                        var (handler, _) = MenuHandlerAndTarget(button);
                        if (handler != "OnRestartButtonClicked") continue;
                        SetLabel(button, "BACK TO LOBBY");
                        Plugin.Log.LogInfo("[GameOver] client retry slot relabeled BACK TO LOBBY (session lobby survives)");
                        RewireClick(button, () =>
                        {
                            try { Traverse.Create(__instance).Field("screen").GetValue<UIScreen>()?.Close(); }
                            catch { }
                            UI.LobbyScreen.ShowOnNextMenuScene = true;
                            MenuLoadLeavesSession.KeepSessionOnce = true; // staying connected is the point
                            MainMenuScene.Load();
                        });
                        break;
                    }
                }
                catch { }
            }
        }

        // The battle-royale WINNER dies too (the self-destruct is how a won match ends), which
        // routes them onto the same screen as everyone else — one that shouts GAME OVER at the
        // person who just won (Omar, 2026-07-29: "the winner's GAME OVER SCREEN should instead
        // say YOU WIN! only losers get GAME OVER"). Retitle it for the winner; losers keep the
        // vanilla screen. Separate from NetRunGameOverButtons because that one exits early for
        // the host, and a winning listen-host deserves the retitle too.
        [HarmonyPatch(typeof(GameOverScreen), "OnGameOver")]
        internal static class WinnerGameOverTitle
        {
            private static void Postfix(GameOverScreen __instance)
            {
                try
                {
                    if (!NetSession.Active) return;
                    if (NetSession.Instance.CurrentMode != Protocol.GameMode.BattleRoyale) return;
                    if (!Modes.BattleRoyale.LocalIsWinner) return;

                    // The title is scene UI, not a serialized field — find the label that says
                    // GAME OVER. Fallback: the biggest non-button, non-stats text on the screen.
                    var stats = Traverse.Create(__instance).Field("statsText").GetValue<TMPro.TMP_Text>();
                    TMPro.TMP_Text title = null;
                    foreach (var t in __instance.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    {
                        if (t == null || ReferenceEquals(t, stats)) continue;
                        if (t.GetComponentInParent<UnityEngine.UI.Button>() != null) continue;
                        string txt = (t.text ?? string.Empty).Trim();
                        if (txt.IndexOf("game over", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        { title = t; break; }
                        if (title == null || t.fontSize > title.fontSize) title = t;
                    }
                    if (title == null)
                    {
                        Plugin.Log.LogWarning("[GameOver] winner retitle: no title label found — the screen keeps GAME OVER");
                        return;
                    }
                    // Kill any localizer that would put GAME OVER back on the next locale refresh.
                    foreach (var comp in title.GetComponents<UnityEngine.MonoBehaviour>())
                        if (comp != null && comp.GetType().Name.Contains("Localiz"))
                            UnityEngine.Object.Destroy(comp);
                    title.text = "YOU WIN!";
                    Plugin.Log.LogInfo("[GameOver] winner's screen retitled YOU WIN!");
                }
                catch (System.Exception e)
                { Plugin.Log.LogWarning($"[GameOver] winner retitle failed: {e.Message}"); }
            }
        }

        // In a net run the suspend-save is blocked (below) and there is no single-player save
        // to come back to — the pause menu's "Save & Exit" would lie. While networking is live
        // it reads just EXIT (localization stripped from that one label); rejoining a still-live
        // session is offered on the PLAY ONLINE screen instead. The RESTART button is removed
        // entirely — retry belongs on the game-over screen.
        [HarmonyPatch(typeof(PauseScreen), "Open")]
        internal static class NetRunPauseButtons
        {
            // Guard the co-op pause overlay against the two ways it softlocks input in a LIVE
            // (non-frozen) world — the tester's "pause + item wheel" report (2026-07-23):
            //   * PauseScreen.Update calls Open() with NO !isOpen guard, so pressing pause again
            //     while already paused re-runs UIScreen.Open, which re-captures previousActionMap as
            //     the CURRENT (UI menu) map — on close the local player is stranded on the menu map
            //     with no menu open and all gameplay input dead. (Vanilla never sees this: frozen
            //     time keeps the pause action from re-firing.)
            //   * Opening pause on top of an already-open item wheel lets two independent input
            //     owners (UIScreen's SwitchCurrentActionMap vs ConsumableWheel's raw Enable/Disable)
            //     fight over the same action maps.
            // Skip the open body in both cases; __state carries that decision to the postfix.
            //
            // A redundant open that came from the PAUSE KEY ITSELF is closed instead of dropped —
            // otherwise ESC opens the overlay and then does nothing at all, which is what a tester
            // hit on 2026-08-07. Vanilla reaches Close() through Update's `else if (isOpen &&
            // backAction)` branch, and only ever gets there because the pause action is DEAD while
            // the overlay is up: UIScreen.Open switched the ship map off, and a disabled action
            // never performs. KeepShipControllableWhilePaused (below) deliberately switches that
            // map back on, so in a net run the pause action does perform, wins the if/else, and
            // arrives here — where suppressing it removed vanilla's own way out. Closing restores
            // the single-player behaviour rather than inventing a new one.
            private static readonly FieldInfo PauseActionField = AccessTools.Field(typeof(PauseScreen), "pauseAction");
            private static readonly FieldInfo PauseShipManagerField = AccessTools.Field(typeof(PauseScreen), "shipManager");

            /// <summary>Was this open triggered by the pause key this frame, rather than by code?
            /// Asked with the game's own query, so programmatic opens (InputSelectorPopup on device
            /// loss) stay plain suppressions and do not toggle the menu shut under the player.</summary>
            private static bool PausePressedThisFrame(PauseScreen screen)
            {
                var action = (PauseActionField?.GetValue(screen) as InputActionReference)?.action;
                var ships = PauseShipManagerField?.GetValue(screen) as ShipManager;
                return action != null && ships != null && ships.WasPerformedThisFrame(action);
            }

            private static bool Prefix(PauseScreen __instance, out bool __state)
            {
                __state = false;
                if (!NetSession.Active) return true; // single-player pause unchanged
                bool alreadyOpen = Traverse.Create(__instance).Field("isOpen").GetValue<bool>();
                if (alreadyOpen || MenuMutex.WheelOpen)
                {
                    __state = true; // redundant/overlapping — skip body AND the postfix re-layout
                    if (alreadyOpen && PausePressedThisFrame(__instance))
                    {
                        Plugin.Log.LogDebug("[Pause] pause key pressed while open — closing (net run)");
                        __instance.Close();
                        return false;
                    }
                    Plugin.Log.LogDebug($"[Pause] suppressed pause open (alreadyOpen={alreadyOpen} wheelOpen={MenuMutex.WheelOpen})");
                    return false;
                }
                return true;
            }

            private static void Postfix(PauseScreen __instance, bool __state)
            {
                if (__state) return; // the prefix suppressed this open
                try
                {
                    if (!NetSession.Active) return; // single-player pause unchanged
                    MenuMutex.PauseOpen = true;
                    // The slot freed by hiding RESTART becomes SEND LOGS: uploads this machine's
                    // log under the shared run id so every player's view of one session lands in
                    // one folder. Toast reports the outcome (sent / saved locally / rate-limited).
                    LayoutMenuColumn(__instance, hideRestart: true, relabelSaveQuitAsExit: true,
                        spareLabel: "SEND LOGS",
                        spareAction: () =>
                        {
                            // null = accepted; the async outcome raises its own toast.
                            var refused = Core.LogUpload.UploadFromUi(NetSession.Instance);
                            if (refused != null) UI.Toast.Show(refused, 5f);
                        });
                    KeepShipControllableWhilePaused();
                }
                catch { }
            }

            // Co-op pause is a non-freezing overlay (PausePolicy suppresses the world-freeze).
            // Vanilla UIScreen.Open also switches the local ship OFF its ShipControlActionMap, which
            // kills movement/aim and — because Ship.Update ties crosshair.Visible to that map's
            // Enabled state — hides the crosshair, leaving the player helpless in a live world.
            // Re-enable the map so gameplay controls and the crosshair survive the overlay; the menu
            // map stays enabled too, so the EXIT/Report buttons remain navigable.
            private static void KeepShipControllableWhilePaused()
            {
                var ship = ShipSync.LocalShip;
                if (ship == null) return;
                foreach (var shipInput in ship.GetComponentsInChildren<ShipInput>(true))
                {
                    // ShipControlActionMap is a ShipActionMap WRAPPER (derives from object, not
                    // InputActionMap), so the old `as InputActionMap` cast was always null and this
                    // whole method silently did nothing — pausing in a net run actually left the
                    // player frozen-input in a live world. Use the wrapper's own Enabled/Enable().
                    var map = shipInput.ShipControlActionMap;
                    if (map != null && !map.Enabled) map.Enable();
                }
            }
        }

        // ---------------------------------------------------------------- shared menu layout

        /// <summary>Remove the restart button from a fixed-slot menu column WITHOUT moving any
        /// button. Each button is an AnimatedScreenElement whose Animator re-drives its RectTransform
        /// to its own prefab slot every frame (the "Visible" open animation), so repositioning never
        /// sticks — the gap kept coming back, and fighting it in LateUpdate was a per-frame tug-of-war.
        /// Instead we leave every button in place and REASSIGN ROLES: the top N physical slots are
        /// relabeled/rewired to the N buttons we keep (top-down order), and the one leftover bottom
        /// slot is hidden. Every surviving role now lives in a slot that was always occupied, so the
        /// visible column is contiguous and the animator can't reopen a gap. Also relabels the
        /// Save&amp;Quit role → EXIT (its suspend-save is blocked in net runs; the run auto-saves).</summary>
        private static void LayoutMenuColumn(UnityEngine.Component root, bool hideRestart,
            bool relabelSaveQuitAsExit = false, string spareLabel = null, System.Action spareAction = null)
        {
            // Physical slots = the menu buttons the game is CURRENTLY showing, top-down. PauseScreen
            // carries five (Resume/Restart/Quit/SaveAndQuit/Report) but only shows a subset.
            var slots = new List<(UnityEngine.UI.Button btn, string handler, UnityEngine.Object target)>();
            foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                if (!button.gameObject.activeInHierarchy) continue; // skip buttons the game hid
                var (handler, target) = MenuHandlerAndTarget(button);
                if (handler == null) continue; // not a menu button (Resume/Restart/Quit/…)
                slots.Add((button, handler, target));
            }
            if (slots.Count == 0) return;
            slots.Sort((a, b) => SlotY(b.btn).CompareTo(SlotY(a.btn))); // top-down

            // Roles to keep, same top-down order, minus restart. Snapshot each role's label and
            // click target NOW — before any relabel/rewire mutates a button we still read below.
            var roles = new List<(string handler, UnityEngine.Object target, string label)>();
            foreach (var s in slots)
            {
                if (hideRestart && s.handler == "OnRestartButtonClicked") continue;
                string label = relabelSaveQuitAsExit && s.handler == "OnSaveAndQuitButtonClicked"
                    ? "EXIT" : LabelOf(s.btn);
                roles.Add((s.handler, s.target, label));
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (i >= roles.Count)
                {
                    // Leftover physical slot (hiding RESTART frees one in a net run). Rather than
                    // deactivate it, hand it to a mod role — it already sits in a real prefab slot,
                    // so it inherits correct position and the open animation instead of fighting
                    // the animator the way an injected button would.
                    if (spareAction != null && !string.IsNullOrEmpty(spareLabel))
                    {
                        SetLabel(slot.btn, spareLabel);
                        RewireClick(slot.btn, spareAction);
                        spareAction = null; // one spare role only
                        continue;
                    }
                    slot.btn.gameObject.SetActive(false);
                    continue;
                }
                var role = roles[i];
                bool sameRole = slot.handler == role.handler && ReferenceEquals(slot.target, role.target);
                if (sameRole)
                {
                    // Already the right button in this slot; only the label may need changing.
                    if (relabelSaveQuitAsExit && role.handler == "OnSaveAndQuitButtonClicked")
                        SetLabel(slot.btn, role.label);
                    continue;
                }
                // Make this slot play the kept role: adopt its label and call its screen method.
                SetLabel(slot.btn, role.label);
                RewireClick(slot.btn, role.target, role.handler);
            }
        }

        private static float SlotY(UnityEngine.UI.Button b) =>
            b.transform is UnityEngine.RectTransform rt ? rt.anchoredPosition.y : b.transform.localPosition.y;

        /// <summary>The button's first "On…ButtonClicked" persistent handler and the object it calls,
        /// or (null, null) if it isn't a menu button (so we never touch unrelated child buttons).</summary>
        private static (string handler, UnityEngine.Object target) MenuHandlerAndTarget(UnityEngine.UI.Button button)
        {
            var ev = button.onClick;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var h = ev.GetPersistentMethodName(i);
                if (!string.IsNullOrEmpty(h) && h.StartsWith("On") && h.EndsWith("ButtonClicked"))
                    return (h, ev.GetPersistentTarget(i));
            }
            return (null, null);
        }

        private static string LabelOf(UnityEngine.UI.Button button)
        {
            var t = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            return t != null ? t.text : "";
        }

        private static void SetLabel(UnityEngine.UI.Button button, string text)
        {
            var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label == null) return;
            // Kill any localizer that would overwrite our text on the next locale refresh.
            foreach (var comp in label.GetComponents<UnityEngine.MonoBehaviour>())
                if (comp != null && comp.GetType().Name.Contains("Localiz"))
                    UnityEngine.Object.Destroy(comp);
            label.text = text;
        }

        // Rewire a slot to invoke another role's screen method directly (via reflection on the
        // captured target), NOT by chaining to another button's onClick — a button we hand a new
        // role to may itself be a remap target whose onClick we clear, which would break a chain.
        /// <summary>Point a menu slot at mod code instead of a vanilla screen method.</summary>
        private static void RewireClick(UnityEngine.UI.Button button, System.Action action)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            button.onClick.AddListener(() => { try { action(); } catch { } });
        }

        private static void RewireClick(UnityEngine.UI.Button button, UnityEngine.Object target, string method)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent(); // drop the old role's call
            if (target == null) return;
            var mi = target.GetType().GetMethod(method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return;
            button.onClick.AddListener(() => { try { mi.Invoke(target, null); } catch { } });
        }

        [HarmonyPatch]
        internal static class NoNetRunSaves
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("Punk.SaveLoad.GameSaver") ?? AccessTools.TypeByName("GameSaver");
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        if (m.Name == "Save")
                            yield return m;
            }

            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                Plugin.Log.LogInfo("[Guard] suspend-save blocked (net run)");
                return false;
            }
        }

        /// <summary>
        /// <c>ShipManager.EnableShipControl</c>/<c>DisableShipControl</c> walk EVERY ship and
        /// dereference <c>ship.shipInput.ShipControlActionMap</c>. In a net run that list contains
        /// other players' PUPPETS, whose input is neutered — so the loop throws partway through and
        /// the ships it had not reached yet keep whatever state they were in.
        ///
        /// The half that hurts is <c>EnableShipControl</c>: it is what vanilla's <c>DebugMenu.Close</c>
        /// calls to give the ship back. If it throws before reaching the local ship, the player is
        /// left permanently unable to fly — Omar, 2026-07-29: "I close it by going into the start menu
        /// and closing the start menu, but then lose ship control". The same call sits behind the debug
        /// menu's Free Move Camera and Teleport Away And Back buttons, so this is not F1-specific.
        ///
        /// Replaced with a loop that skips puppets, tolerates a missing action map per ship, and
        /// cannot abandon the remaining ships because one entry was unusable.
        /// </summary>
        [HarmonyPatch]
        internal static class ShipControlLoopsSurvivePuppets
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("ShipManager");
                if (t == null) yield break;
                foreach (var name in new[] { "EnableShipControl", "DisableShipControl" })
                {
                    var m = AccessTools.Method(t, name);
                    if (m != null) yield return m;
                }
            }

            private static bool Prefix(ShipManager __instance, MethodBase __originalMethod)
            {
                if (!NetSession.Active) return true;   // solo: vanilla is correct and cheaper
                bool enable = __originalMethod.Name == "EnableShipControl";
                int touched = 0, skipped = 0;
                foreach (var ship in __instance.Ships)
                {
                    if (ship == null) { skipped++; continue; }
                    // A puppet is driven by replication; it has no local input to enable, and
                    // touching it is exactly what threw.
                    if (ship.GetComponent<RemotePuppet>() != null) { skipped++; continue; }
                    try
                    {
                        var map = ship.shipInput != null ? ship.shipInput.ShipControlActionMap : null;
                        if (map == null) { skipped++; continue; }
                        if (enable) map.Enable(); else map.Disable();
                        touched++;
                    }
                    catch { skipped++; }   // never let one ship strand the others
                }
                if (skipped > 0 && NetDiag.Enabled)
                    NetDiag.Throttled("shipcontrol", 5f, "Guard",
                        () => $"{__originalMethod.Name}: {touched} ship(s) applied, {skipped} skipped (puppets/no input)");
                return false;
            }
        }
    }
}
