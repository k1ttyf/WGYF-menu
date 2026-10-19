using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CasinoMenu
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed partial class CasinoMenuPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.wgyf.menu";
        public const string PluginName = "k1ttyf/WGYF-menu";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static CasinoMenuPlugin Instance;
        internal static Harmony Harmony;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> RequireHost;
        internal static ConfigEntry<bool> NoMoneySpend;
        internal static ConfigEntry<bool> NoTicketSpend;
        internal static ConfigEntry<float> MoneyGainMultiplier;
        internal static ConfigEntry<float> MovementMultiplier;
        internal static ConfigEntry<float> JumpMultiplier;
        internal static ConfigEntry<float> NoClipSpeed;
        internal static ConfigEntry<float> GameSpeed;
        internal static ConfigEntry<float> DayTimerDelta;
        internal static ConfigEntry<bool> PauseDayTimer;
        internal static ConfigEntry<bool> HealAfterSceneLoad;
        internal static ConfigEntry<bool> AutoUpdaterEnabled;

        internal static ConfigEntry<KeyboardShortcut> ToggleMenu;
        internal static ConfigEntry<KeyboardShortcut> TriggerWin;
        internal static ConfigEntry<KeyboardShortcut> AddMoneyKey;
        internal static ConfigEntry<KeyboardShortcut> RemoveMoneyKey;
        internal static ConfigEntry<KeyboardShortcut> AddTicketsKey;
        internal static ConfigEntry<KeyboardShortcut> MeetQuotaKey;
        internal static ConfigEntry<KeyboardShortcut> HealKey;
        internal static ConfigEntry<KeyboardShortcut> NoClipToggle;
        internal static ConfigEntry<KeyboardShortcut> NoClipUp;
        internal static ConfigEntry<KeyboardShortcut> NoClipDown;

        private bool _menuOpen;
        private bool _noClip;
        private Rect _window = new Rect(20, 20, 920, 680);
        private Vector2 _tabScroll;
        private string _status = "Загрузка...";
        private float _originalGameSpeed = 1f;
        private bool _gameSpeedCaptured;
        private int _activeTab;
        private readonly Dictionary<string, string> _editBuffers = new Dictionary<string, string>();
        private readonly Dictionary<PlayerSettings, PlayerSettingsSnapshot> _settings = new Dictionary<PlayerSettings, PlayerSettingsSnapshot>();

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            BindConfig();
            InitProgression();
            InitWorld();
            InitInterface();
            Harmony = new Harmony(PluginGuid);
            try { Harmony.PatchAll(); }
            catch
            {
                Harmony.UnpatchSelf();
                enabled = false;
                throw;
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            _status = "Готово. Insert — меню";
            Log.LogInfo(PluginName + " " + PluginVersion + " loaded for Gamble With Your Friends (Mono).");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Harmony != null) Harmony.UnpatchSelf();
            RestorePlayerSettings();
            SetNoClip(false);
            RestoreGameSpeed();
            ShutdownProgression();
            ShutdownWorld();
            ShutdownInterface();
        }

        private void BindConfig()
        {
            Enabled = Config.Bind("General", "Enabled", true, "Включить мод.");
            RequireHost = Config.Bind("General", "RequireHost", true, "Серверные операции работают только у активного host/server.");
            AutoUpdaterEnabled = Config.Bind("General", "AutoUpdaterEnabled", false, "Резерв настройки для официального endpoint обновлений.");
            NoMoneySpend = Config.Bind("Currency", "NoMoneySpend", false, "Возвращать ставки денег.");
            NoTicketSpend = Config.Bind("Currency", "NoTicketSpend", false, "Не уменьшать билеты.");
            MoneyGainMultiplier = Config.Bind("Currency", "MoneyGainMultiplier", 1f, new ConfigDescription("Множитель выигрыша.", new AcceptableValueRange<float>(1f, 10f)));
            MovementMultiplier = Config.Bind("Self", "MovementMultiplier", 1f, new ConfigDescription("Скорость ходьбы и спринта.", new AcceptableValueRange<float>(0.5f, 5f)));
            JumpMultiplier = Config.Bind("Self", "JumpMultiplier", 1f, new ConfigDescription("Высота прыжка.", new AcceptableValueRange<float>(0.5f, 5f)));
            NoClipSpeed = Config.Bind("Self", "NoClipSpeed", 3f, new ConfigDescription("Множитель скорости noclip относительно обычной скорости игрока.", new AcceptableValueRange<float>(0.5f, 10f)));
            GameSpeed = Config.Bind("Time", "GameSpeed", 1f, new ConfigDescription("Глобальная скорость игры для host.", new AcceptableValueRange<float>(0.25f, 5f)));
            DayTimerDelta = Config.Bind("Time", "DayTimerDelta", 60f, new ConfigDescription("Шаг изменения таймера дня.", new AcceptableValueRange<float>(5f, 600f)));
            PauseDayTimer = Config.Bind("Time", "PauseDayTimer", false, "Заморозить таймер дня у host.");
            HealAfterSceneLoad = Config.Bind("Self", "HealAfterSceneLoad", true, "Повторять восстановление органов после загрузки сцены.");

            ToggleMenu = Config.Bind("Keybinds", "MenuToggle", new KeyboardShortcut(KeyCode.Insert), "Открыть меню.");
            TriggerWin = Config.Bind("Keybinds", "TriggerWin", new KeyboardShortcut(KeyCode.F6), "Победа в ближайшей игре.");
            AddMoneyKey = Config.Bind("Keybinds", "AddMoney", new KeyboardShortcut(KeyCode.F7), "Добавить деньги.");
            RemoveMoneyKey = Config.Bind("Keybinds", "RemoveMoney", new KeyboardShortcut(KeyCode.F8), "Убрать деньги.");
            AddTicketsKey = Config.Bind("Keybinds", "AddTickets", new KeyboardShortcut(KeyCode.F9), "Добавить билеты.");
            MeetQuotaKey = Config.Bind("Keybinds", "MeetQuota", new KeyboardShortcut(KeyCode.F10), "Закрыть текущую квоту.");
            HealKey = Config.Bind("Keybinds", "Heal", new KeyboardShortcut(KeyCode.F11), "Восстановить органы.");
            NoClipToggle = Config.Bind("Keybinds", "NoClipToggle", new KeyboardShortcut(KeyCode.F12), "Переключить noclip.");
            NoClipUp = Config.Bind("Keybinds", "NoClipUp", new KeyboardShortcut(KeyCode.Space), "Noclip вверх.");
            NoClipDown = Config.Bind("Keybinds", "NoClipDown", new KeyboardShortcut(KeyCode.LeftControl), "Noclip вниз.");
        }

        private void Update()
        {
            TickInterface();
            if (!Enabled.Value)
            {
                if (_noClip) SetNoClip(false);
                RestorePlayerSettings();
                RestoreGameSpeed();
                TickProgression();
                return;
            }
            if (_movementEnabled.Value || _jumpEnabled.Value) ApplyPlayerModifiers();
            else RestorePlayerSettings();
            ApplyGameSpeed();
            TickProgression();
            TickWorld();
        }

        private void OnGUI()
        {
            if (!_menuOpen) return;
            _window = GUI.Window(GetInstanceID(), _window, DrawWindow, PluginName);
            _textInputFocused = GUI.GetNameOfFocusedControl().StartsWith("CasinoMenu.", StringComparison.Ordinal);
        }

        private void DrawWindow(int id)
        {
            if (!Enabled.Value)
            {
                GUILayout.Label("Casino Menu disabled. Re-enable it below.");
                DrawSettingsTab();
                GUI.DragWindow(new Rect(0, 0, _window.width, 22));
                return;
            }
            GUILayout.Label((NetworkServer.active ? "Host/server" : "Client / offline") + " | " + _status);
            var tabs = new[] { "Day", "Money", "Quota", "Floors", "Players", "Items", "Perks", "Self", "Game", "Advanced", "Keybinds", "Settings" };
            var tab = GUILayout.SelectionGrid(_activeTab, tabs, 6);
            if (tab != _activeTab) { _activeTab = tab; _tabScroll = Vector2.zero; GUI.FocusControl(null); }
            _tabScroll = GUILayout.BeginScrollView(_tabScroll, GUILayout.Height(Mathf.Min(540, Screen.height - 160)));
            GUILayout.BeginVertical();
            switch (_activeTab)
            {
                case 0: DrawDayTab(); break;
                case 1: DrawMoneyTab(); break;
                case 2: DrawQuotaTab(); break;
                case 3: DrawFloorsTab(); break;
                case 4: DrawPlayersTab(); break;
                case 5: DrawItemsTab(); break;
                case 6: DrawPerksTab(); break;
                case 7: DrawSelfTab(); break;
                case 8: DrawGameTab(); break;
                case 9: DrawAdvancedTab(); break;
                case 10: DrawKeybindsTab(); break;
                case 11: DrawSettingsTab(); break;
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close")) SetMenuOpen(false);
            GUI.DragWindow(new Rect(0, 0, _window.width, 22));
        }

        internal static bool HostAllowed()
        {
            return NetworkServer.active;
        }

        private static PlayerController LocalController()
        {
            return UnityEngine.Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None).FirstOrDefault(x => x != null && x.isLocalPlayer);
        }

        private static PlayerProfile LocalProfile()
        {
            var pc = LocalController();
            if (pc == null) return null;
            return pc.GetComponent<PlayerProfile>();
        }

        private static BigNumber Number(double value)
        {
            return BigNumber.FromDouble(value);
        }

        internal static void AddMoney(double value)
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var manager = MoneyManager.Instance;
            if (manager == null) { SetStatus("MoneyManager не найден"); return; }
            manager.TryChangeBalance(Number(value), LocalProfile(), ChangeType.Misc);
            SetStatus("Добавлено денег: " + value.ToString("0"));
        }

        internal static void RemoveMoney(double value)
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var manager = MoneyManager.Instance;
            if (manager == null) { SetStatus("MoneyManager не найден"); return; }
            manager.TryChangeBalance(Number(-value), LocalProfile(), ChangeType.Misc);
            SetStatus("Убрано денег: " + value.ToString("0"));
        }

        internal static void AddTickets(long value)
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var manager = MoneyManager.Instance;
            if (manager == null) { SetStatus("MoneyManager не найден"); return; }
            manager.TryChangeTicketBalance(value);
            SetStatus("Добавлено билетов: " + value);
        }

        private void MeetQuota()
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var manager = MoneyManager.Instance;
            var game = GameManager.Instance;
            if (manager == null || game == null) { SetStatus("MoneyManager/GameManager не найден"); return; }
            var need = game.currentQuota - manager.balance;
            if (need > BigNumber.Zero) manager.TryChangeBalance(need, LocalProfile(), ChangeType.Misc);
            SetStatus("Квота закрыта");
        }

        private void TriggerNearbyWin()
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var player = LocalController();
            var profile = LocalProfile();
            if (player == null || profile == null) { SetStatus("Локальный игрок не найден"); return; }
            var target = UnityEngine.Object.FindObjectsByType<GameBase>(FindObjectsSortMode.None)
                .Where(x => x != null && Vector3.Dot((player.head == null ? player.transform : player.head.transform).forward,
                    x.transform.position - player.transform.position) > 0f)
                .OrderBy(x => Vector3.Distance(x.transform.position, player.transform.position))
                .FirstOrDefault();
            if (target == null || Vector3.Distance(target.transform.position, player.transform.position) > 8f)
            {
                SetStatus("Рядом нет игры");
                return;
            }
            target.ServerSimulatePayout(profile, true);
            SetStatus("Победа вызвана: " + target.GameName);
        }

        private void HealLocalPlayer()
        {
            var pc = LocalController();
            var organs = pc == null ? null : pc.GetComponent<PlayerOrgans>();
            if (organs == null) { SetStatus("Органы игрока не найдены"); return; }
            if (!HostAllowed()) { SetStatus("Для синхронного лечения нужен host/server"); return; }
            organs.ServerSetBodyParts(BuildOrganData(organs, true, true, true, true));
            SetStatus("Органы восстановлены");
        }

        private void HealOrgan(OrganType organ)
        {
            var pc = LocalController();
            var organs = pc == null ? null : pc.GetComponent<PlayerOrgans>();
            if (organs == null) { SetStatus("Органы игрока не найдены"); return; }
            if (!HostAllowed()) { SetStatus("Для синхронного лечения нужен host/server"); return; }
            var left = ReadOrgan(organs, "_localLeftEye");
            var right = ReadOrgan(organs, "_localRightEye");
            var body = ReadOrgan(organs, "_localBody");
            var mouth = ReadOrgan(organs, "_localMouth");
            if (organ == OrganType.LeftEye) left = true;
            if (organ == OrganType.RightEye) right = true;
            if (organ == OrganType.Body) body = true;
            if (organ == OrganType.Mouth) mouth = true;
            organs.ServerSetBodyParts(BuildOrganData(organs, left, right, body, mouth));
            SetStatus("Восстановлено: " + organ);
        }

        private void TriggerNearbyLoss()
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var player = LocalController();
            if (player == null) { SetStatus("Локальный игрок не найден"); return; }
            var target = UnityEngine.Object.FindObjectsByType<GameBase>(FindObjectsSortMode.None)
                .Where(x => x != null && Vector3.Dot((player.head == null ? player.transform : player.head.transform).forward,
                    x.transform.position - player.transform.position) > 0f)
                .OrderBy(x => Vector3.Distance(x.transform.position, player.transform.position))
                .FirstOrDefault();
            if (target == null || Vector3.Distance(target.transform.position, player.transform.position) > 8f)
            { SetStatus("Рядом нет игры"); return; }
            target.ServerSimulatePayout(LocalProfile(), false);
            SetStatus("Вызвано поражение: " + target.GameName);
        }

        private static PlayerOrganData BuildOrganData(PlayerOrgans organs, bool left, bool right, bool body, bool mouth)
        {
            return new PlayerOrganData
            {
                organsReference = organs,
                leftEye = left,
                rightEye = right,
                body = body,
                mouth = mouth
            };
        }

        private static bool ReadOrgan(PlayerOrgans organs, string fieldName)
        {
            var field = typeof(PlayerOrgans).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field != null && field.GetValue(organs) is bool value && value;
        }

        private void AdjustDayTimer(float delta)
        {
            if (!HostAllowed()) { SetStatus("Нужен активный host/server"); return; }
            var game = GameManager.Instance;
            if (game == null) { SetStatus("GameManager не найден"); return; }
            game.ServerAdjustTimer(delta);
            SetStatus("Таймер изменён на " + delta.ToString("0") + " сек.");
        }

        private void ApplyPlayerModifiers()
        {
            var pc = LocalController();
            if (pc == null) return;
            var settingsField = typeof(PlayerController).GetField("_ps", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var settings = settingsField == null ? null : settingsField.GetValue(pc) as PlayerSettings;
            if (settings == null) return;
            PlayerSettingsSnapshot snapshot;
            if (!_settings.TryGetValue(settings, out snapshot))
            {
                snapshot = new PlayerSettingsSnapshot(settings);
                _settings[settings] = snapshot;
            }
            settings.maxSpeed = snapshot.MaxSpeed * (_movementEnabled == null || _movementEnabled.Value ? MovementMultiplier.Value : 1f);
            settings.sprintMaxSpeed = snapshot.SprintMaxSpeed * (_movementEnabled == null || _movementEnabled.Value ? MovementMultiplier.Value : 1f);
            settings.acceleration = snapshot.Acceleration * (_movementEnabled == null || _movementEnabled.Value ? MovementMultiplier.Value : 1f);
            settings.jumpForce = snapshot.JumpForce * (_jumpEnabled == null || _jumpEnabled.Value ? JumpMultiplier.Value : 1f);
        }

        private void RestorePlayerSettings()
        {
            foreach (var pair in _settings) pair.Value.Restore(pair.Key);
            _settings.Clear();
        }

        private void SetNoClip(bool enabled)
        {
            SetNativeNoClip(enabled);
        }

        private float GetNoClipBaseSpeed(PlayerController pc)
        {
            var settingsField = typeof(PlayerController).GetField(
                "_ps",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var settings = settingsField == null ? null : settingsField.GetValue(pc) as PlayerSettings;
            if (settings != null)
            {
                PlayerSettingsSnapshot snapshot;
                if (_settings.TryGetValue(settings, out snapshot)) return snapshot.MaxSpeed;
                return settings.maxSpeed;
            }
            return 5f;
        }

        private void ApplyGameSpeed()
        {
            if (!HostAllowed()) return;
            var settings = Resources.FindObjectsOfTypeAll<GameSettings>().FirstOrDefault();
            if (settings == null) return;
            if (!_gameSpeedCaptured)
            {
                _originalGameSpeed = settings.timeScale;
                _gameSpeedCaptured = true;
            }
            settings.SetTimeScale(GameSpeed.Value);
        }

        private void RestoreGameSpeed()
        {
            var settings = Resources.FindObjectsOfTypeAll<GameSettings>().FirstOrDefault();
            if (settings != null && _gameSpeedCaptured) settings.SetTimeScale(_originalGameSpeed);
            _gameSpeedCaptured = false;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_noClip) SetNoClip(false);
            RestorePlayerSettings();
            SetMenuOpen(false);
            _editBuffers.Clear();
            if (Enabled.Value && HealAfterSceneLoad.Value && NetworkServer.active) StartCoroutine(HealAfterSceneLoadRoutine());
            if (Enabled.Value) Log.LogInfo("Scene loaded: " + scene.name);
        }

        private System.Collections.IEnumerator HealAfterSceneLoadRoutine()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            if (Enabled.Value && NetworkServer.active) HealLocalPlayer();
        }

        private void SpawnItem(SpawnableSO item)
        {
            var player = LocalController();
            if (player == null || item == null || item.prefab == null)
            {
                SetStatus("Item prefab or local player not found");
                return;
            }

            var position = player.transform.position + player.transform.forward * 2.5f + Vector3.up * 0.5f;
            if (!NetworkServer.active)
            {
                var console = Find<NewConsole>();
                if (!NetworkClient.active || console == null) { SetStatus("Нужно подключение и NewConsole"); return; }
                InvokeGame(console, "RequestSpawnPrefab", item.spawnableID, position, 1);
                SetStatus("Spawn requested: " + item.spawnableName);
                return;
            }
            var spawned = UnityEngine.Object.Instantiate(item.prefab, position, Quaternion.identity);
            NetworkServer.Spawn(spawned, (NetworkConnectionToClient)null);

            // Keep the same bookkeeping as the game's normal item-spawn path.
            var manager = ItemManager.Instance;
            if (manager != null)
            {
                manager.ServerAddItem(item);
                var consumable = spawned.GetComponent<ConsumableItem>();
                if (consumable != null && manager.spawnedItemInstances != null)
                    manager.spawnedItemInstances.Add(consumable);
            }
            var stamps = Find<ItemStampManager>();
            if (stamps != null) stamps.MarkInstancePurchased(spawned);
            SetStatus("Spawned: " + item.spawnableName);
        }

        private static List<SpawnableSO> FindSpawnables()
        {
            var settings = Resources.Load<SpawnableSettings>("SpawnableSettings");
            if (settings == null)
            {
                settings = Resources.FindObjectsOfTypeAll<SpawnableSettings>().FirstOrDefault();
            }

            if (settings != null && settings.spawnables != null)
            {
                return settings.spawnables
                    .Where(x => x != null && x.prefab != null)
                    .GroupBy(x => x.spawnableID)
                    .Select(x => x.First())
                    .OrderBy(x => x.spawnableID)
                    .ToList();
            }

            return new List<SpawnableSO>();
        }
        private static void SetStatus(string message)
        {
            if (Instance != null) Instance._status = message;
            if (Log != null && (Instance == null || Instance._interfaceLiveLog == null || Instance._interfaceLiveLog.Value)) Log.LogInfo(message);
        }

        private sealed class PlayerSettingsSnapshot
        {
            internal readonly float MaxSpeed;
            internal readonly float SprintMaxSpeed;
            internal readonly float Acceleration;
            internal readonly float JumpForce;

            internal PlayerSettingsSnapshot(PlayerSettings settings)
            {
                MaxSpeed = settings.maxSpeed;
                SprintMaxSpeed = settings.sprintMaxSpeed;
                Acceleration = settings.acceleration;
                JumpForce = settings.jumpForce;
            }

            internal void Restore(PlayerSettings settings)
            {
                settings.maxSpeed = MaxSpeed;
                settings.sprintMaxSpeed = SprintMaxSpeed;
                settings.acceleration = Acceleration;
                settings.jumpForce = JumpForce;
            }
        }

        [HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.TryChangeBalance))]
        private static class MoneyPatch
        {
            private static void Prefix(ref BigNumber amount, ChangeType changeType)
            {
                if (!Enabled.Value || !NetworkServer.active) return;
                if (NoMoneySpend.Value && amount < BigNumber.Zero) amount = BigNumber.Zero;
                if (changeType == ChangeType.GameResult && amount > BigNumber.Zero && MoneyGainMultiplier.Value > 1f) amount *= MoneyGainMultiplier.Value;
            }
        }

        [HarmonyPatch(typeof(MoneyManager), nameof(MoneyManager.TryChangeTicketBalance))]
        private static class TicketPatch
        {
            private static void Prefix(ref long amount)
            {
                if (!Enabled.Value || !NetworkServer.active) return;
                if (NoTicketSpend.Value && amount < 0) amount = 0;
            }
        }

        [HarmonyPatch(typeof(GameManager), "SetTimer")]
        private static class TimerPatch
        {
            private static bool Prefix()
            {
                return !(Enabled.Value && PauseDayTimer.Value && NetworkServer.active);
            }
        }

        [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
        private static class NoClipFixedUpdatePatch
        {
            private static bool Prefix(PlayerController __instance)
            {
                return Instance == null || !Instance._noClip || __instance == null || !__instance.isLocalPlayer;
            }
        }
    }
}
