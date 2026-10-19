using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace CasinoMenu
{
    // The main plugin is kept in CasinoMenuPlugin.cs.  This file contains the
    // world-facing tabs so game API changes stay isolated from the menu shell.
    public sealed partial class CasinoMenuPlugin
    {
        private ConfigEntry<bool> _worldUnlockFloors;
        private ConfigEntry<bool> _worldHarmlessBats;
        private ConfigEntry<bool> _worldHarmlessTasers;
        private ConfigEntry<bool> _worldElevatorLocked;
        private ConfigEntry<float> _worldBuffDuration;
        private string _worldItemSearch = "";
        private string _worldSeedText = "0";
        private string _worldCutsceneText = "0";
        private string _worldFloorText = "0";
        private string _worldMaxPlayersText = "4";
        private bool _worldResetSaveArmed;
        private ElevatorManager _worldUnlockedElevator;
        private readonly HashSet<uint> _worldBlind = new HashSet<uint>();
        private readonly HashSet<uint> _worldMute = new HashSet<uint>();
        private readonly HashSet<uint> _worldNoBody = new HashSet<uint>();
        private readonly HashSet<uint> _worldHeadLock = new HashSet<uint>();
        private readonly Dictionary<uint, bool[]> _worldOriginalOrgans = new Dictionary<uint, bool[]>();
        private readonly List<WorldBuffTimer> _worldBuffTimers = new List<WorldBuffTimer>();

        private void InitWorld()
        {
            _worldUnlockFloors = Config.Bind("World", "UnlockAllFloors", false, "Keep all elevator buttons enabled.");
            _worldHarmlessBats = Config.Bind("World", "HarmlessBats", false, "Host: disable bat knockback for players, NPCs and items.");
            _worldHarmlessTasers = Config.Bind("World", "HarmlessTasers", false, "Host: disable taser charge use and betting multiplier changes.");
            _worldElevatorLocked = Config.Bind("World", "LockElevator", false, "Lock elevator movement.");
            _worldBuffDuration = Config.Bind("World", "BuffDuration", 45f, new ConfigDescription("Duration of an administered perk.", new AcceptableValueRange<float>(5f, 300f)));
        }

        private void TickWorld()
        {
            if (_worldUnlockFloors == null) return;
            var elevator = Find<ElevatorManager>();
            if (elevator != null && _worldUnlockFloors.Value && _worldUnlockedElevator != elevator && NetworkServer.active)
            {
                elevator.RpcEnableAllButtons();
                _worldUnlockedElevator = elevator;
            }
            if (!_worldUnlockFloors.Value) _worldUnlockedElevator = null;
            _worldBuffTimers.RemoveAll(timer => timer.Owner == null);
        }

        private void ShutdownWorld()
        {
            if (NetworkServer.active)
            {
                foreach (var player in WorldPlayers())
                {
                    var id = WorldId(player);
                    bool[] original;
                    if (_worldOriginalOrgans.TryGetValue(id, out original)) WorldOrgans(player, original[0], original[1], original[2], original[3]);
                    if (_worldHeadLock.Contains(id)) player.ServerLockHead(false);
                }
            }
            _worldBlind.Clear();
            _worldMute.Clear();
            _worldNoBody.Clear();
            _worldHeadLock.Clear();
            _worldOriginalOrgans.Clear();
            _worldUnlockedElevator = null;
        }

        private List<PlayerController> WorldPlayers()
        {
            return UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None)
                .Where(p => p != null).OrderBy(p => p.isLocalPlayer ? 0 : 1).ToList();
        }

        private static uint WorldId(PlayerController player)
        {
            var identity = player == null ? null : player.GetComponent<NetworkIdentity>();
            return identity == null ? 0u : identity.netId;
        }

        private static string WorldPlayerName(PlayerController player)
        {
            var profile = player == null ? null : player.GetComponent<PlayerProfile>();
            return profile == null || string.IsNullOrEmpty(profile.playerName) ? "Player" : profile.playerName;
        }

        private void WorldOrgans(PlayerController player, bool left, bool right, bool body, bool mouth)
        {
            if (player == null) return;
            var organs = player.GetComponentInChildren<PlayerOrgans>();
            if (organs == null) return;
            RunHost(() => organs.ServerSetBodyParts(new PlayerOrganData
            {
                organsReference = organs, leftEye = left, rightEye = right, body = body, mouth = mouth
            }));
        }

        private bool[] WorldGetOriginalOrgans(PlayerController player)
        {
            var id = WorldId(player);
            bool[] original;
            if (_worldOriginalOrgans.TryGetValue(id, out original)) return original;
            var organs = player.GetComponentInChildren<PlayerOrgans>();
            if (organs == null) return new[] { true, true, true, true };
            original = new[] { ReadOrgan(organs, "_localLeftEye"), ReadOrgan(organs, "_localRightEye"), ReadOrgan(organs, "_localBody"), ReadOrgan(organs, "_localMouth") };
            _worldOriginalOrgans[id] = original;
            return original;
        }

        private void WorldSetToggle(HashSet<uint> set, PlayerController player, bool enabled, Action<bool> apply)
        {
            if (player == null || !NetworkServer.active) return;
            var id = WorldId(player);
            if (enabled) set.Add(id); else set.Remove(id);
            apply(enabled);
        }

        private void WorldApplyPranks(PlayerController player)
        {
            var id = WorldId(player);
            var original = WorldGetOriginalOrgans(player);
            var blind = _worldBlind.Contains(id);
            var mute = _worldMute.Contains(id);
            var noBody = _worldNoBody.Contains(id);
            WorldOrgans(player, original[0] && !blind, original[1] && !blind, original[2] && !noBody, original[3] && !mute);
            if (!blind && !mute && !noBody) _worldOriginalOrgans.Remove(id);
        }

        private void WorldPlayerAction(PlayerController player, string action)
        {
            if (player == null) return;
            var inventory = player.GetComponent<PlayerInventory>();
            if (action == "freeze") RunHost(() => player.ServerLock(true));
            else if (action == "unfreeze") RunHost(() => player.ServerLock(false));
            else if (action == "ragdoll") RunHost(() => player.ServerKnockback(Vector3.zero, Vector3.zero));
            else if (action == "wake") RunHost(player.ServerWakeUp);
            else if (action == "knock") RunHost(() => player.ServerKnockback(UnityEngine.Random.onUnitSphere * 14f, UnityEngine.Random.onUnitSphere * 5f));
            else if (action == "disarm" && inventory != null) RunHost(inventory.ServerThrowItemRandomly);
            else if (action == "noclip")
            {
                if (!player.isLocalPlayer) { SetStatus("Native noclip is local-only in this game build."); return; }
                SetNativeNoClip(!_nativeNoClip);
            }
            else if (action == "tp")
            {
                var local = LocalController();
                if (local != null && player != local)
                {
                    var position = player.transform.position + Vector3.up * 0.6f;
                    if (NetworkServer.active) local.ServerTeleport(position); else local.LocalTeleport(position);
                }
            }
            else if (action == "summon")
            {
                var local = LocalController();
                if (local != null && player != local)
                    RunHost(() => player.ServerTeleport(local.transform.position + local.transform.forward * 1.5f + Vector3.up * 0.5f));
            }
            else if (action == "kick")
            {
                var identity = player.GetComponent<NetworkIdentity>();
                if (identity != null && identity.connectionToClient != null) RunHost(identity.connectionToClient.Disconnect);
            }
        }

        private void WorldPrank(PlayerController player, string action, bool enabled)
        {
            if (player == null || !NetworkServer.active) return;
            if (action == "head") WorldSetToggle(_worldHeadLock, player, enabled, v => player.ServerLockHead(v));
            else
            {
                var set = action == "blind" ? _worldBlind : action == "mute" ? _worldMute : _worldNoBody;
                WorldSetToggle(set, player, enabled, v => { });
                WorldApplyPranks(player);
            }
        }

        private void WorldHealPlayer(PlayerController player)
        {
            if (player == null || !NetworkServer.active) return;
            var id = WorldId(player);
            _worldBlind.Remove(id);
            _worldMute.Remove(id);
            _worldNoBody.Remove(id);
            _worldOriginalOrgans.Remove(id);
            WorldOrgans(player, true, true, true, true);
        }

        private void DrawPlayersTab()
        {
            var players = WorldPlayers();
            GUILayout.Label("Players (" + players.Count + ")");
            GUILayout.BeginHorizontal();
            Button("Freeze all", () => RunHost(() => players.ForEach(p => p.ServerLock(true))), true);
            Button("Unfreeze all", () => RunHost(() => players.ForEach(p => p.ServerLock(false))), true);
            Button("Ragdoll all", () => RunHost(() => players.ForEach(p => WorldPlayerAction(p, "ragdoll"))), true);
            Button("Wake all", () => RunHost(() => players.ForEach(p => p.ServerWakeUp())), true);
            Button("Knock all", () => RunHost(() => players.ForEach(p => WorldPlayerAction(p, "knock"))), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("Disarm all", () => RunHost(() => players.ForEach(p => { var i = p.GetComponent<PlayerInventory>(); if (i != null) i.ServerThrowItemRandomly(); })), true);
            Button("Heal all", () => RunHost(() => players.ForEach(WorldHealPlayer)), true);
            Button("Summon all", () => { var local = LocalController(); if (local != null) RunHost(() => players.ForEach(p => { if (p != local) p.ServerTeleport(local.transform.position + UnityEngine.Random.insideUnitSphere * 1.5f); })); }, true);
            Button("Kick others", () => RunHost(() => players.ForEach(p => { if (!p.isLocalPlayer) WorldPlayerAction(p, "kick"); })), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("Blind others", () => RunHost(() => players.ForEach(p => { if (!p.isLocalPlayer) WorldPrank(p, "blind", true); })), true);
            Button("Unblind all", () => RunHost(() => players.ForEach(p => WorldPrank(p, "blind", false))), true);
            Button("No mouth: others", () => RunHost(() => players.ForEach(p => { if (!p.isLocalPlayer) WorldPrank(p, "mute", true); })), true);
            Button("Restore mouths", () => RunHost(() => players.ForEach(p => WorldPrank(p, "mute", false))), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("No body: others", () => RunHost(() => players.ForEach(p => { if (!p.isLocalPlayer) WorldPrank(p, "body", true); })), true);
            Button("Restore bodies", () => RunHost(() => players.ForEach(p => WorldPrank(p, "body", false))), true);
            Button("Lock heads: others", () => RunHost(() => players.ForEach(p => { if (!p.isLocalPlayer) WorldPrank(p, "head", true); })), true);
            Button("Free all heads", () => RunHost(() => players.ForEach(p => WorldPrank(p, "head", false))), true);
            GUILayout.EndHorizontal();
            GUILayout.Label("Noclip is local-only. No mouth applies the game's voice effect; it does not disable voice chat.");
            foreach (var player in players)
            {
                GUILayout.BeginVertical("box");
                GUILayout.Label(WorldPlayerName(player) + (player.isLocalPlayer ? " (you)" : ""));
                GUILayout.BeginHorizontal();
                Button("Freeze", () => WorldPlayerAction(player, "freeze"), true);
                Button("Unfreeze", () => WorldPlayerAction(player, "unfreeze"), true);
                Button("Ragdoll", () => WorldPlayerAction(player, "ragdoll"), true);
                Button("Wake", () => WorldPlayerAction(player, "wake"), true);
                Button("Knock", () => WorldPlayerAction(player, "knock"), true);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                Button("Disarm", () => WorldPlayerAction(player, "disarm"), true);
                if (player.isLocalPlayer) Button("Native noclip", () => WorldPlayerAction(player, "noclip"), false);
                Button("TP to them", () => WorldPlayerAction(player, "tp"), false);
                if (!player.isLocalPlayer) Button("Bring here", () => WorldPlayerAction(player, "summon"), true);
                if (!player.isLocalPlayer) Button("Kick", () => WorldPlayerAction(player, "kick"), true);
                GUILayout.EndHorizontal();
                var id = WorldId(player);
                GUILayout.BeginHorizontal();
                Button(_worldBlind.Contains(id) ? "Unblind" : "Blind", () => WorldPrank(player, "blind", !_worldBlind.Contains(id)), true);
                Button(_worldMute.Contains(id) ? "Restore mouth" : "No mouth", () => WorldPrank(player, "mute", !_worldMute.Contains(id)), true);
                Button(_worldNoBody.Contains(id) ? "Restore body" : "No body", () => WorldPrank(player, "body", !_worldNoBody.Contains(id)), true);
                Button(_worldHeadLock.Contains(id) ? "Free head" : "Lock head", () => WorldPrank(player, "head", !_worldHeadLock.Contains(id)), true);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        private void DrawItemsTab()
        {
            GUILayout.Label("Item rules");
            RegisterToggle("world.harmless.bats", "Harmless bats", _worldHarmlessBats);
            RegisterToggle("world.harmless.tasers", "Harmless tasers", _worldHarmlessTasers);
            GUILayout.BeginHorizontal();
            Button("Remove bats", () => RunHost(() => UnityEngine.Object.FindObjectsByType<Bat>(FindObjectsSortMode.None).ToList().ForEach(x => x.DestroyItem())), true);
            Button("Remove tasers", () => RunHost(() => UnityEngine.Object.FindObjectsByType<Taser>(FindObjectsSortMode.None).ToList().ForEach(x => x.DestroyItem())), true);
            Button("Remove all", () => RunHost(() => UnityEngine.Object.FindObjectsByType<Item>(FindObjectsSortMode.None).ToList().ForEach(WorldDeleteItem)), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("Reset items", () => RunHost(() => { var m = Find<ItemManager>(); if (m != null) m.ServerResetItems(); }), true);
            Button("Reroll stamps", () => RunHost(() => { var m = Find<ItemStampManager>(); if (m != null) m.RerollAllItemStamps(); }), true);
            Button("Respawn stamps", () => RunHost(() => { var m = Find<ItemStampManager>(); if (m != null) m.RetrieveAndRespawnAllItemStamps(); }), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("Throw held items", () => RunHost(() => WorldPlayers().ForEach(p => { var i = p.GetComponent<PlayerInventory>(); if (i != null) i.ServerThrowItemRandomly(); })), true);
            Button("Delete held items", () => RunHost(() => WorldPlayers().ForEach(p => { var inventory = p.GetComponent<PlayerInventory>(); if (inventory != null) WorldDeleteItem(inventory.ServerDropHoldingItem()); })), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search");
            GUI.SetNextControlName("CasinoMenu.world.item.search");
            _worldItemSearch = GUILayout.TextField(_worldItemSearch ?? "");
            if (GUILayout.Button("Clear", GUILayout.Width(55))) _worldItemSearch = "";
            GUILayout.EndHorizontal();
            var query = (_worldItemSearch ?? "").ToLowerInvariant();
            foreach (var item in FindSpawnables().Where(x => string.IsNullOrEmpty(query) || (x.spawnableName ?? "").ToLowerInvariant().Contains(query)))
                Button(item.spawnableName + " [" + item.spawnableID + "]", () => SpawnItem(item), false);
        }

        private void DrawPerksTab()
        {
            if (!HostAllowed()) { GUILayout.Label("Host only: perks are server state."); return; }
            Slider("Duration", _worldBuffDuration, 5f, 300f);
            foreach (PlayerBuffType type in Enum.GetValues(typeof(PlayerBuffType)))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(type.ToString());
                Button("Give", () => RunHost(() => WorldPlayers().ForEach(p => { var b = p.GetComponent<PlayerBuff>(); if (b != null) b.ApplyBuff(type, 1f, _worldBuffDuration.Value); })), true);
                Button("Clear", () => RunHost(() => WorldPlayers().ForEach(p => WorldClearBuff(p.GetComponent<PlayerBuff>(), type))), true);
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            Button("Fill energy", () => RunHost(() => WorldPlayers().ForEach(p => { var e = p.GetComponent<PlayerEnergy>(); if (e != null) e.AddEnergy(9999f); })), true);
            Button("Drain energy", () => RunHost(() => WorldPlayers().ForEach(p => { var e = p.GetComponent<PlayerEnergy>(); if (e != null) e.DecreaseEnergy(9999f); })), true);
            GUILayout.EndHorizontal();
            var upgrades = Find<UpgradeManager>();
            if (upgrades != null)
            {
                foreach (PlayerUpgradeType type in Enum.GetValues(typeof(PlayerUpgradeType)))
                {
                    GUILayout.BeginHorizontal(); GUILayout.Label(type.ToString());
                    Button("+", () => RunHost(() => WorldPlayers().ForEach(p => { var profile = p.GetComponent<PlayerProfile>(); if (profile != null) upgrades.ChangeUpgradeData(profile.steamId, type, 1f); })), true);
                    Button("-", () => RunHost(() => WorldPlayers().ForEach(p => { var profile = p.GetComponent<PlayerProfile>(); if (profile != null) upgrades.ChangeUpgradeData(profile.steamId, type, -1f); })), true);
                    Button("Reset", () => RunHost(upgrades.ServerResetAllUpgradesToDefaults), true);
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawFloorsTab()
        {
            var elevator = Find<ElevatorManager>();
            var game = Find<GameManager>();
            if (elevator == null) { GUILayout.Label("Elevator not found"); return; }
            RegisterToggle("world.unlock.floors", "Unlock all floors", _worldUnlockFloors);
            RegisterToggle("world.lock.elevator", "Lock elevator", _worldElevatorLocked);
            GUILayout.Label("Current floor: " + (game == null ? "-" : game.currentFloor.ToString()) + " | Elevator: " + (elevator.IsLocked || _worldElevatorLocked.Value ? "locked" : "ready"));
            var floors = GetField(elevator, "allFloors") as IList;
            GUILayout.BeginHorizontal();
            for (var i = 0; floors != null && i < floors.Count; i++) { var floor = i; Button("F" + i, () => RunHost(() => WorldTeleportFloor(elevator, floor)), true); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            _worldFloorText = EditText("world.floor", "Custom floor", _worldFloorText);
            Button("Go", () => { int floor; if (int.TryParse(_worldFloorText, out floor)) RunHost(() => WorldTeleportFloor(elevator, floor)); }, true);
            GUILayout.EndHorizontal();
        }

        private void DrawGameTab()
        {
            var game = Find<GameManager>();
            var save = Find<SaveManager>();
            GUILayout.Label(game == null ? "Not in a game." : string.Format("State: {0} | Floor {1} | Timer {2:0}s | Day {3} ({4} left) | Quota {5}", game.state, game.currentFloor, game.RoundTimerRemaining, game.daysPassed + 1, game.daysLeft, game.currentQuota));
            if (NetworkServer.active) GUILayout.Label("Players: " + (NetworkServer.connections == null ? 0 : NetworkServer.connections.Count));
            GUILayout.BeginHorizontal();
            Button("Lobby", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Lobby); }), true);
            Button("Game", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Game); }), true);
            Button("Win", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Win); }), true);
            Button("Lose", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Lose); }), true);
            Button("Summary", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Summary); }), true);
            Button("Test", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Test); }), true);
            Button("Cutscene", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.Cutscene); }), true);
            Button("Follow Us", () => RunHost(() => { if (game != null) game.ServerSetScene(GameState.FollowUs); }), true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("End day", () => RunHost(() => { if (game != null) game.ServerAdjustTimer(-10000000f); }), true);
            Button("Save", () => RunHost(() => { if (save != null) save.SaveGame(); }), true);
            Button("Load", () => WorldReloadSave(save), true);
            Button(_worldResetSaveArmed ? "Confirm reset" : "Reset save", () => { if (!_worldResetSaveArmed) _worldResetSaveArmed = true; else { _worldResetSaveArmed = false; WorldResetCurrentSave(save); } }, true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            var lobby = Find<LobbyManager>();
            Button("Private", () => RunHost(() => { if (lobby != null) lobby.SetLobbyPrivacy(true); }), true);
            Button("Public", () => RunHost(() => { if (lobby != null) lobby.SetLobbyPrivacy(false); }), true);
            Button("Invite friend", () => RunAction(() => { if (lobby != null) lobby.InviteFriend(); }), false);
            GUILayout.EndHorizontal();
            _worldMaxPlayersText = EditText("world.maxplayers", "Max players", _worldMaxPlayersText);
            Button("Apply player limit", () => { int max; if (int.TryParse(_worldMaxPlayersText, out max)) RunHost(() => WorldSetPlayerLimit(lobby, max)); }, true);
            GUILayout.Label("World");
            Button("Clear NPCs", () => RunHost(() => { var n = Find<NPCSpawner>(); if (n != null) n.DestroyAllNPCs(); }), true);
            Button("Move NPCs to me", () => { var local = LocalController(); if (local != null) RunHost(() => UnityEngine.Object.FindObjectsByType<NPC>(FindObjectsSortMode.None).ToList().ForEach(n => n.Warp(local.transform.position + UnityEngine.Random.insideUnitSphere * 2f))); }, true);
            GUILayout.BeginHorizontal();
            _worldCutsceneText = EditText("world.cutscene", "Cutscene", _worldCutsceneText);
            Button("Play", () => { int index; if (int.TryParse(_worldCutsceneText, out index)) RunHost(() => { if (game != null) game.ServerSetCutscene(index); }); }, true);
            Button("0", () => RunHost(() => { if (game != null) game.ServerSetCutscene(0); }), true);
            Button("1", () => RunHost(() => { if (game != null) game.ServerSetCutscene(1); }), true);
            Button("2", () => RunHost(() => { if (game != null) game.ServerSetCutscene(2); }), true);
            GUILayout.EndHorizontal();
            _worldSeedText = EditText("world.seed", "Seed", _worldSeedText);
            Button("Set seed", () => { int seed; if (int.TryParse(_worldSeedText, out seed)) RunHost(() => { var random = Find<SeededRandomManager>(); if (random != null) random.InitializeSeed(seed); }); }, true);
        }

        private void WorldSetPlayerLimit(LobbyManager lobby, int count)
        {
            if (lobby == null || !NetworkServer.active) return;
            count = Mathf.Clamp(count, 1, 16);
            if (NetworkManager.singleton != null) NetworkManager.singleton.maxConnections = count;
            NetworkServer.maxConnections = count;
            var settings = GetField(lobby, "lobbySettings") as LobbySettings;
            if (settings != null)
            {
                settings.maxPlayers = count;
                settings.NotifyChanged();
            }
            try
            {
                var api = Type.GetType("Steamworks.SteamMatchmaking, Facepunch.Steamworks.Win64") ?? Type.GetType("Steamworks.SteamMatchmaking, Assembly-CSharp");
                if (api == null) api = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Steamworks.SteamMatchmaking", false)).FirstOrDefault(t => t != null);
                var method = api == null ? null : api.GetMethod("SetLobbyMemberLimit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var idGetter = typeof(LobbyManager).GetMethod("GetCurrentLobbyID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var id = idGetter == null ? null : idGetter.Invoke(lobby, null);
                if (method == null || id == null) { SetStatus("Game player limit updated; platform lobby API is unavailable."); return; }
                var applied = method.Invoke(null, new object[] { id, count });
                if (applied is bool && !(bool)applied) SetStatus("Game player limit updated; platform lobby rejected the change.");
            }
            catch (Exception ex) { if (Log != null) Log.LogWarning("Unable to apply lobby player limit: " + ex.Message); }
        }

        private void WorldDeleteItem(Item item)
        {
            // Players also derive from Item through PlayerCarry. They are never loot.
            if (item == null || item is PlayerCarry || !NetworkServer.active) return;
            var consumable = item as ConsumableItem;
            if (consumable != null) { consumable.DestroyItem(); return; }
            if (item.GetComponent<NetworkIdentity>() == null) return;
            item.ServerDrop();
            NetworkServer.Destroy(item.gameObject);
        }

        private void WorldTeleportFloor(ElevatorManager elevator, int floor)
        {
            if (elevator == null || !NetworkServer.active) return;
            var floors = GetField(elevator, "allFloors") as IList;
            if (floors == null || floor < 0 || floor >= floors.Count) { SetStatus("Floor is outside the loaded elevator range."); return; }
            if (_worldElevatorLocked.Value) { SetStatus("Unlock the elevator first."); return; }
            elevator.ServerForceTeleportPlayers(floor);
        }

        private void WorldClearBuff(PlayerBuff buff, PlayerBuffType type)
        {
            if (buff == null || !NetworkServer.active) return;
            // ApplyBuff adds its amount. Cancel only this type's pending native
            // subtraction before restoring the server's synchronized baseline.
            foreach (var timer in _worldBuffTimers.Where(t => t.Owner == buff && t.Type == type).ToArray())
            {
                buff.StopCoroutine(timer);
                timer.Dispose();
            }
            var baseline = type == PlayerBuffType.TipsyFortune ? 1f : 0f;
            buff.ApplyBuff(type, baseline - buff.GetValue(type), 0f);
        }

        private sealed class WorldBuffTimer : IEnumerator, IDisposable
        {
            internal readonly PlayerBuff Owner;
            internal readonly PlayerBuffType Type;
            private readonly CasinoMenuPlugin _plugin;
            private readonly IEnumerator _routine;
            private bool _disposed;

            internal WorldBuffTimer(CasinoMenuPlugin plugin, PlayerBuff owner, PlayerBuffType type, IEnumerator routine)
            {
                _plugin = plugin; Owner = owner; Type = type; _routine = routine;
                plugin._worldBuffTimers.Add(this);
            }

            public object Current { get { return _routine.Current; } }
            public bool MoveNext()
            {
                if (_disposed) return false;
                var next = _routine.MoveNext();
                if (!next) Dispose();
                return next;
            }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _plugin._worldBuffTimers.Remove(this);
                var disposable = _routine as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        [HarmonyPatch(typeof(PlayerBuff), "RemoveBuffAfter")]
        private static class TrackBuffExpiryPatch
        {
            private static void Postfix(PlayerBuff __instance, PlayerBuffType __0, ref IEnumerator __result)
            {
                if (Instance != null && NetworkServer.active && __result != null)
                    __result = new WorldBuffTimer(Instance, __instance, __0, __result);
            }
        }

        private static bool WorldRuleActive(ConfigEntry<bool> rule)
        {
            return Instance != null && Enabled != null && Enabled.Value && NetworkServer.active && rule != null && rule.Value;
        }

        [HarmonyPatch]
        private static class HarmlessBatPatch
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                // Mirror invokes the woven UserCode methods on the host even
                // when the attacker is an unmodified remote client.
                yield return AccessTools.Method(typeof(Bat), "UserCode_CmdHitPlayer__PlayerController__Single");
                yield return AccessTools.Method(typeof(Bat), "UserCode_CmdHitNpc__NPC__Single");
                yield return AccessTools.Method(typeof(Bat), "UserCode_CmdHitItem__Item__Single");
            }
            private static bool Prefix() { return !WorldRuleActive(Instance == null ? null : Instance._worldHarmlessBats); }
        }

        [HarmonyPatch(typeof(Taser), "UseCharge")]
        private static class HarmlessTaserPatch
        {
            // UseTaser is only an animation; UseCharge performs the server effect.
            private static bool Prefix() { return !WorldRuleActive(Instance == null ? null : Instance._worldHarmlessTasers); }
        }

        [HarmonyPatch]
        private static class LockElevatorPatch
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ElevatorManager), "ServerTryTeleportPlayers");
                yield return AccessTools.Method(typeof(ElevatorManager), "ServerForceTeleportPlayers");
            }
            // Keep the game's own lock state intact, including cutscene locks.
            private static bool Prefix() { return !WorldRuleActive(Instance == null ? null : Instance._worldElevatorLocked); }
        }
    }
}
