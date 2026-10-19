using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace CasinoMenu
{
    // Host progression and rules controls.  This is deliberately a partial so that the
    // menu/input code can remain independent from game-version-specific progression code.
    public sealed partial class CasinoMenuPlugin
    {
        private ConfigEntry<bool> _progressionEnabled;
        private ConfigEntry<bool> _overrideDayLength;
        private ConfigEntry<float> _dayLengthSeconds;
        private ConfigEntry<bool> _infiniteDays;
        private ConfigEntry<bool> _autoMoney;
        private ConfigEntry<string> _autoMoneyAmount;
        private ConfigEntry<bool> _autoBailout;
        private ConfigEntry<float> _bailoutPercent;
        private ConfigEntry<bool> _overrideQuota;
        private ConfigEntry<float> _quotaMultiplier;
        private ConfigEntry<bool> _lossShield;
        private ConfigEntry<bool> _perSaveRules;
        private ConfigEntry<int> _daysBeforeQuota;
        private ConfigEntry<int> _dailyTicketReward;
        private ConfigEntry<float> _catchUpFactor;
        private ConfigEntry<long> _startingMoney;
        private ConfigEntry<long> _startingTickets;
        private ConfigEntry<long> _startingQuota;
        private ConfigEntry<int> _npcCount;
        private ConfigEntry<bool> _challengeTools;

        private float _nextAutoMoney;
        private bool _bailoutUsedToday;
        private int _bailoutDay = int.MinValue;
        private ConfigEntry<int> _saveBailoutDay;
        private string _loadedSaveName;
        private bool _rulesLoadedForSave;
        private GameManager _lastProgressionManager;
        private bool _previousPerSaveEnabled;
        private float _nextRuleSnapshot;
        private ConfigEntry<float> _saveDayLength;
        private ConfigEntry<float> _saveQuotaMultiplier;
        private ConfigEntry<int> _saveNpcCount;
        private ConfigEntry<int> _saveDaysBeforeQuota;
        private ConfigEntry<int> _saveDailyTickets;
        private ConfigEntry<float> _saveCatchUp;
        private ConfigEntry<bool> _saveInfiniteDays;
        private ConfigEntry<bool> _saveAutoMoney;
        private ConfigEntry<string> _saveAutoMoneyAmount;
        private ConfigEntry<bool> _saveAutoBailout;
        private ConfigEntry<float> _saveBailoutPercent;
        private ConfigEntry<bool> _saveLossShield;
        private ConfigEntry<bool> _saveChallengeTools;
        private ConfigEntry<float> _saveTimerDelta;
        private ConfigEntry<long> _saveStartingMoney;
        private ConfigEntry<long> _saveStartingTickets;
        private ConfigEntry<long> _saveStartingQuota;
        private ConfigEntry<bool> _saveOverrideDayLength;
        private ConfigEntry<bool> _saveOverrideQuota;
        private ConfigEntry<bool> _savePauseDayTimer;
        private ConfigEntry<float> _saveGameSpeed;
        private ConfigEntry<bool> _saveNoMoneySpend;
        private ConfigEntry<bool> _saveNoTicketSpend;
        private ConfigEntry<float> _saveMoneyGainMultiplier;
        private ConfigEntry<float> _saveAppliedQuotaMultiplier;
        private ConfigEntry<string> _saveQuotaFingerprint;
        private GameSettings _rulesSettings;
        private int _vanillaNpc;
        private int _vanillaDays;
        private int _vanillaTickets;
        private float _vanillaDayLength;
        private float _vanillaCatchUp;
        private long _vanillaStartingMoney;
        private long _vanillaStartingTickets;
        private long _vanillaStartingQuota;
        private bool _vanillaRulesCaptured;
        private Vector2 _challengeScroll;
        private Vector2 _challengeCatalogScroll;
        private readonly Dictionary<ConfigEntryBase, ConfigEntryBase> _globalProgressionRules = new Dictionary<ConfigEntryBase, ConfigEntryBase>();
        private float _liveQuotaMultiplier = 1f;
        private bool _liveQuotaInitialized;
        private float _dayDurationBeforeOverride;
        private bool _dayDurationApplied;

        private void InitProgression()
        {
            _progressionEnabled = Config.Bind("Progression", "Enabled", true, "Enable day, money, quota and advanced host controls.");
            _overrideDayLength = Config.Bind("Progression", "OverrideDayLength", false, "Use the configured day duration on the host.");
            _dayLengthSeconds = Config.Bind("Progression", "DayLengthSeconds", 300f, new ConfigDescription("Day duration in seconds.", new AcceptableValueRange<float>(1f, 3600f)));
            _infiniteDays = Config.Bind("Progression", "InfiniteDays", false, "Keep at least 99 days remaining.");
            _autoMoney = Config.Bind("Progression", "AutoMoney", false, "Periodically add money to the shared balance.");
            _autoMoneyAmount = Config.Bind("Progression", "AutoMoneyAmount", "125000", "Money granted every two seconds (BigNumber text).");
            _autoBailout = Config.Bind("Progression", "AutoBailout", false, "Once per day, inject a fraction of the quota when balance reaches zero.");
            _bailoutPercent = Config.Bind("Progression", "BailoutPercent", 0.10f, new ConfigDescription("Bailout fraction of current quota.", new AcceptableValueRange<float>(0.01f, 1f)));
            _overrideQuota = Config.Bind("Progression", "OverrideQuota", false, "Apply a multiplier to the current quota and newly generated quotas.");
            _quotaMultiplier = Config.Bind("Progression", "QuotaMultiplier", 1f, new ConfigDescription("Quota multiplier.", new AcceptableValueRange<float>(0.01f, 100f)));
            _lossShield = Config.Bind("Progression", "LossShield", false, "Block host transitions to the lose scene (used by the loss patch).");
            _perSaveRules = Config.Bind("Progression", "PerSaveRules", true, "Remember advanced rules for each save name in the plugin config.");
            _challengeTools = Config.Bind("Progression", "ChallengeTools", true, "Enable challenge administration controls.");
            _daysBeforeQuota = Config.Bind("AdvancedRules", "DaysBeforeQuota", -1, "Override days per quota; negative keeps vanilla.");
            _dailyTicketReward = Config.Bind("AdvancedRules", "DailyTicketReward", -1, "Override daily ticket reward; negative keeps vanilla.");
            _catchUpFactor = Config.Bind("AdvancedRules", "CatchUpFactor", -1f, "Override catch-up factor; negative keeps vanilla.");
            _startingMoney = Config.Bind("AdvancedRules", "StartingMoney", -1L, "Override starting money; negative keeps vanilla.");
            _startingTickets = Config.Bind("AdvancedRules", "StartingTickets", -1L, "Override starting tickets; negative keeps vanilla.");
            _startingQuota = Config.Bind("AdvancedRules", "StartingQuota", -1L, "Override starting quota; negative keeps vanilla.");
            _npcCount = Config.Bind("AdvancedRules", "NpcCount", -1, "Override NPC crowd size; negative keeps vanilla.");
            RememberGlobalRule(_dayLengthSeconds); RememberGlobalRule(_quotaMultiplier);
            RememberGlobalRule(_overrideDayLength); RememberGlobalRule(_overrideQuota);
            RememberGlobalRule(_npcCount); RememberGlobalRule(_daysBeforeQuota);
            RememberGlobalRule(_dailyTicketReward); RememberGlobalRule(_catchUpFactor);
            RememberGlobalRule(_infiniteDays); RememberGlobalRule(_autoMoney);
            RememberGlobalRule(_autoMoneyAmount); RememberGlobalRule(_autoBailout);
            RememberGlobalRule(_bailoutPercent); RememberGlobalRule(_lossShield);
            RememberGlobalRule(_challengeTools); RememberGlobalRule(DayTimerDelta);
            RememberGlobalRule(PauseDayTimer); RememberGlobalRule(GameSpeed);
            RememberGlobalRule(NoMoneySpend); RememberGlobalRule(NoTicketSpend); RememberGlobalRule(MoneyGainMultiplier);
            RememberGlobalRule(_startingMoney); RememberGlobalRule(_startingTickets); RememberGlobalRule(_startingQuota);
            _previousPerSaveEnabled = _perSaveRules.Value;
            if (_perSaveRules.Value) RestoreGlobalProgressionRules();
            CaptureVanillaRules();
            RegisterAction("progression_add_money", "Add money", () => AddMoney(1000));
            RegisterAction("progression_meet_quota", "Meet quota", MeetQuotaMoney);
            RegisterAction("progression_complete_quota", "Complete quota", CompleteCurrentQuota);
            RegisterAction("progression_end_day", "End day", EndCurrentDay);
            RegisterAction("progression_progress_floor", "Progress floor", ProgressFloor);
        }

        private void ShutdownProgression()
        {
            SaveRulesForCurrentSave(_lastProgressionManager);
            RestoreRuntimeDayLength();
            RestoreVanillaRules();
            if (_rulesLoadedForSave) RestoreGlobalProgressionRules();
            _rulesLoadedForSave = false;
            _loadedSaveName = null;
            _lastProgressionManager = null;
        }

        private void TickProgression()
        {
            var gm = Find<GameManager>();
            if (_progressionEnabled == null || !_progressionEnabled.Value || Enabled == null || !Enabled.Value || gm == null || !NetworkServer.active)
            {
                if (_lastProgressionManager != null || _rulesLoadedForSave) ShutdownProgression();
                return;
            }
            DetectProgressionSaveChange(gm);
            LoadRulesForCurrentSave(gm);
            ApplyRuntimeDayLength(gm);
            ApplyLiveQuotaMultiplier(gm);
            if (_infiniteDays.Value && gm.NetworkdaysLeft < 99) gm.NetworkdaysLeft = 99;

            _bailoutUsedToday = _bailoutDay == gm.NetworkdaysPassed;
            var mm = MoneyManager.Instance;
            if (mm != null)
            {
                if (_autoMoney.Value && Time.unscaledTime >= _nextAutoMoney)
                {
                    _nextAutoMoney = Time.unscaledTime + 2f;
                    BigNumber amount;
                    if (TryParseBigNumber(_autoMoneyAmount.Value, out amount) && amount > BigNumber.Zero)
                        mm.TryChangeBalance(amount, LocalProfile(), ChangeType.Misc);
                }
                if (_autoBailout.Value && gm.HasDayStarted && !_bailoutUsedToday && mm.balance <= BigNumber.Zero)
                {
                    var grant = gm.currentQuota * (double)Mathf.Clamp01(_bailoutPercent.Value);
                    if (grant <= BigNumber.Zero) grant = BigNumber.One;
                    if (mm.TryChangeBalance(grant, LocalProfile(), ChangeType.Misc))
                    {
                        _bailoutUsedToday = true;
                        _bailoutDay = gm.NetworkdaysPassed;
                        if (_saveBailoutDay != null) _saveBailoutDay.Value = _bailoutDay;
                    }
                }
            }
            if (Time.unscaledTime >= _nextRuleSnapshot)
            {
                _nextRuleSnapshot = Time.unscaledTime + 1f;
                SaveRulesForCurrentSave(gm);
                if (!_rulesLoadedForSave) SaveGlobalProgressionRules();
            }
        }

        private void DetectProgressionSaveChange(GameManager gm)
        {
            var sm = SaveManager.Instance;
            var name = sm == null ? null : sm.CurrentSaveName;
            if (_loadedSaveName != name || _lastProgressionManager != gm || _previousPerSaveEnabled != _perSaveRules.Value)
            {
                SaveRulesForCurrentSave(_lastProgressionManager);
                if (_rulesLoadedForSave) RestoreGlobalProgressionRules();
                else SaveGlobalProgressionRules();
                RestoreRuntimeDayLength();
                RestoreVanillaRules();
                _loadedSaveName = name;
                _lastProgressionManager = gm;
                _previousPerSaveEnabled = _perSaveRules.Value;
                _rulesLoadedForSave = false;
                _liveQuotaInitialized = false;
                _dayDurationApplied = false;
                _nextAutoMoney = Time.unscaledTime;
                _saveBailoutDay = string.IsNullOrEmpty(name) ? null : Config.Bind("SaveRules." + SanitizeSection(name), "BailoutUsedDay", int.MinValue);
                _saveAppliedQuotaMultiplier = string.IsNullOrEmpty(name) ? null : Config.Bind("SaveRules." + SanitizeSection(name), "SavedQuotaMultiplier", -1f);
                _saveQuotaFingerprint = string.IsNullOrEmpty(name) ? null : Config.Bind("SaveRules." + SanitizeSection(name), "SavedQuotaFingerprint", "");
                _bailoutDay = _saveBailoutDay == null ? int.MinValue : _saveBailoutDay.Value;
                _bailoutUsedToday = _bailoutDay == gm.NetworkdaysPassed;
                ClearProgressionEditBuffers();
                if (!_perSaveRules.Value) ApplyAdvancedRules();
            }
        }

        private void RememberGlobalRule<T>(ConfigEntry<T> rule)
        {
            _globalProgressionRules[rule] = Config.Bind("GlobalRules." + rule.Definition.Section, rule.Definition.Key, rule.Value);
        }

        private void SaveGlobalProgressionRules()
        {
            foreach (var rule in _globalProgressionRules) rule.Value.BoxedValue = rule.Key.BoxedValue;
        }

        private void RestoreGlobalProgressionRules()
        {
            foreach (var rule in _globalProgressionRules) rule.Key.BoxedValue = rule.Value.BoxedValue;
        }

        private void ClearProgressionEditBuffers()
        {
            foreach (var key in new[] { "npc_count", "days_per_quota", "daily_ticket_reward", "starting_money", "starting_tickets", "starting_quota", "auto_money_amount", "day_length_input", "days_left", "required_quota", "current_quota" })
                _editBuffers.Remove(key);
        }

        private void ApplyRuntimeDayLength(GameManager gm)
        {
            if (!_overrideDayLength.Value) { RestoreRuntimeDayLength(); return; }
            if (!_dayDurationApplied)
            {
                _dayDurationBeforeOverride = gm.Network_dayDuration;
                _dayDurationApplied = true;
            }
            gm.Network_dayDuration = Mathf.Clamp(_dayLengthSeconds.Value, 1f, 3600f);
        }

        private void RestoreRuntimeDayLength()
        {
            if (_dayDurationApplied && _lastProgressionManager != null && NetworkServer.active)
                _lastProgressionManager.Network_dayDuration = _dayDurationBeforeOverride;
            _dayDurationApplied = false;
        }

        private void ApplyLiveQuotaMultiplier(GameManager gm)
        {
            var multiplier = _overrideQuota.Value ? Mathf.Clamp(_quotaMultiplier.Value, 0.01f, 100f) : 1f;
            if (_liveQuotaInitialized && !Mathf.Approximately(multiplier, _liveQuotaMultiplier))
                gm.currentQuota = BigNumber.Max(BigNumber.One, gm.currentQuota * ((double)multiplier / _liveQuotaMultiplier));
            _liveQuotaMultiplier = multiplier;
            _liveQuotaInitialized = true;
        }

        private static string QuotaFingerprint(BigNumber quota, int daysPassed, int successfulQuota)
        {
            return quota.Mantissa.ToString("R", CultureInfo.InvariantCulture) + ":" + quota.Exponent.ToString(CultureInfo.InvariantCulture) +
                ":" + daysPassed.ToString(CultureInfo.InvariantCulture) + ":" + successfulQuota.ToString(CultureInfo.InvariantCulture);
        }

        private void CaptureVanillaRules()
        {
            _rulesSettings = Resources.Load<GameSettings>("GameSettings");
            if (_rulesSettings == null || _vanillaRulesCaptured) return;
            _vanillaNpc = _rulesSettings.npcCount;
            _vanillaDays = _rulesSettings.daysBeforeQuota;
            _vanillaTickets = _rulesSettings.dailyTicketReward;
            _vanillaDayLength = _rulesSettings.dayDuration;
            _vanillaCatchUp = _rulesSettings.catchUpFactor;
            _vanillaStartingMoney = _rulesSettings.startingMoney;
            _vanillaStartingTickets = _rulesSettings.startingTicket;
            _vanillaStartingQuota = _rulesSettings.startingQuota;
            _vanillaRulesCaptured = true;
        }

        private void ApplyAdvancedRules()
        {
            if (!NetworkServer.active) return;
            var gs = Resources.Load<GameSettings>("GameSettings");
            if (gs == null) return;
            if (!_vanillaRulesCaptured) { _rulesSettings = gs; CaptureVanillaRules(); }
            gs.npcCount = _npcCount.Value >= 0 ? _npcCount.Value : _vanillaNpc;
            gs.daysBeforeQuota = _daysBeforeQuota.Value >= 0 ? _daysBeforeQuota.Value : _vanillaDays;
            gs.dailyTicketReward = _dailyTicketReward.Value >= 0 ? _dailyTicketReward.Value : _vanillaTickets;
            gs.catchUpFactor = _catchUpFactor.Value >= 0f ? _catchUpFactor.Value : _vanillaCatchUp;
            gs.startingMoney = _startingMoney.Value >= 0 ? _startingMoney.Value : _vanillaStartingMoney;
            gs.startingTicket = _startingTickets.Value >= 0 ? _startingTickets.Value : _vanillaStartingTickets;
            gs.startingQuota = _startingQuota.Value >= 0 ? _startingQuota.Value : _vanillaStartingQuota;
            gs.NotifyChanged();
        }

        private void RestoreVanillaRules()
        {
            if (!_vanillaRulesCaptured || _rulesSettings == null) return;
            _rulesSettings.npcCount = _vanillaNpc;
            _rulesSettings.daysBeforeQuota = _vanillaDays;
            _rulesSettings.dailyTicketReward = _vanillaTickets;
            _rulesSettings.dayDuration = _vanillaDayLength;
            _rulesSettings.catchUpFactor = _vanillaCatchUp;
            _rulesSettings.startingMoney = _vanillaStartingMoney;
            _rulesSettings.startingTicket = _vanillaStartingTickets;
            _rulesSettings.startingQuota = _vanillaStartingQuota;
            _rulesSettings.NotifyChanged();
        }

        private void DrawDayTab()
        {
            var gm = Find<GameManager>();
            GUILayout.Label("Day");
            RegisterToggle("progression_day", "Override day length", _overrideDayLength);
            Slider("Day length (seconds)", _dayLengthSeconds, 1f, 3600f);
            RegisterToggle("progression_inf_days", "Infinite days", _infiniteDays);
            if (gm != null)
            {
                GUILayout.Label("Days left: " + gm.NetworkdaysLeft + "   Timer: " + gm.RoundTimerRemaining.ToString("0.0"));
                GUILayout.BeginHorizontal();
                Button("- time", () => gm.ServerAdjustTimer(-DayTimerDelta.Value));
                Button("+ time", () => gm.ServerAdjustTimer(DayTimerDelta.Value));
                Button("End day", EndCurrentDay);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                Button("Days -", () => gm.NetworkdaysLeft = Mathf.Max(0, gm.NetworkdaysLeft - 1));
                Button("Days +", () => { if (gm.NetworkdaysLeft < int.MaxValue) gm.NetworkdaysLeft++; });
                Button("Set 99", () => gm.NetworkdaysLeft = 99);
                GUILayout.EndHorizontal();
                var daysText = EditText("days_left", "Set days left", gm.NetworkdaysLeft.ToString(CultureInfo.InvariantCulture));
                int daysLeft;
                if (int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out daysLeft) && daysLeft >= 0)
                    Button("Apply days left", () => gm.NetworkdaysLeft = daysLeft);
                var newLengthText = EditText("day_length_input", "Set day length (seconds)", _dayLengthSeconds.Value.ToString(CultureInfo.InvariantCulture));
                float newLength;
                if (float.TryParse(newLengthText, NumberStyles.Float, CultureInfo.InvariantCulture, out newLength) && newLength > 0f && !float.IsInfinity(newLength))
                    Button("Apply day length", () => { _dayLengthSeconds.Value = Mathf.Clamp(newLength, 1f, 3600f); _overrideDayLength.Value = true; ApplyRuntimeDayLength(gm); });
            }
            Slider("Game speed", GameSpeed, 0.25f, 5f);
            RegisterToggle("progression_pause", "Pause day timer", PauseDayTimer);
        }

        private void DrawMoneyTab()
        {
            var mm = MoneyManager.Instance;
            GUILayout.Label("Money");
            var amountText = EditText("money_amount", "Amount", "1000");
            BigNumber amount = BigNumber.Zero;
            var validAmount = TryParseBigNumber(amountText, out amount) && amount >= BigNumber.Zero;
            RegisterToggle("progression_auto_money", "Auto money", _autoMoney);
            _autoMoneyAmount.Value = EditText("auto_money_amount", "Auto amount", _autoMoneyAmount.Value);
            BigNumber autoAmount;
            if (!TryParseBigNumber(_autoMoneyAmount.Value, out autoAmount) || autoAmount < BigNumber.Zero) _autoMoneyAmount.Value = "0";
            if (mm != null) GUILayout.Label("Balance: " + mm.balance);
            GUILayout.BeginHorizontal();
            if (validAmount)
            {
                if (mm != null)
                {
                    Button("Add amount", () => RunHost(() => mm.TryChangeBalance(amount, LocalProfile(), ChangeType.Misc)));
                    Button("Remove amount", () => RunHost(() => mm.TryChangeBalance(-amount, LocalProfile(), ChangeType.Misc)));
                    Button("Set amount", () => RunHost(() => mm.SetBalance(amount, LocalProfile(), ChangeType.Misc)));
                }
            }
            Button("+1K", () => AddMoney(1000));
            Button("+1M", () => AddMoney(1000000));
            Button("+1B", () => AddMoney(1000000000));
            if (mm != null) Button("Set 0", () => mm.SetBalance(BigNumber.Zero, LocalProfile(), ChangeType.Misc));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Button("+1 trillion", () => AddMoney(1000000000000d));
            Button("+1 quadrillion", () => AddMoney(1000000000000000d));
            GUILayout.EndHorizontal();
            RegisterToggle("progression_refund_money", "Refund every money loss", NoMoneySpend);
            RegisterToggle("progression_refund_tickets", "Refund every ticket loss", NoTicketSpend);
            Slider("Payout multiplier", MoneyGainMultiplier, 1f, 10f);
            RegisterToggle("progression_bailout", "Daily bailout", _autoBailout);
            Slider("Bailout fraction", _bailoutPercent, 0.01f, 1f);
            Button("Meet quota (money)", MeetQuotaMoney);
            Button("Complete quota", CompleteCurrentQuota);
            Button("Progress floor", ProgressFloor);
            Button("Pay debt", () => InvokeGame(Find<GameManager>(), "ServerPayDebt"));
            if (mm != null) Button("Reset balances", mm.CmdResetBalancesToDefault);
            GUILayout.Label("Tickets: " + (mm == null ? "-" : mm.ticketBalance.ToString(CultureInfo.InvariantCulture)));
            var ticketText = EditText("ticket_amount", "Ticket amount", "10");
            long ticketAmount;
            var validTickets = long.TryParse(ticketText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticketAmount) && ticketAmount >= 0;
            GUILayout.BeginHorizontal();
            if (mm != null && validTickets)
            {
                Button("Add tickets", () => mm.TryChangeTicketBalance(ticketAmount));
                Button("Remove tickets", () => mm.TryChangeTicketBalance(-ticketAmount));
                Button("Set tickets", () => mm.TrySetTicketBalance(ticketAmount));
            }
            if (mm != null) Button("+999 tickets", () => mm.TrySetTicketBalance(999));
            GUILayout.EndHorizontal();
        }

        private void DrawQuotaTab()
        {
            var gm = Find<GameManager>();
            GUILayout.Label("Quota");
            RegisterToggle("progression_quota", "Scale current and future quotas", _overrideQuota);
            Slider("Quota multiplier", _quotaMultiplier, 0.01f, 100f);
            RegisterToggle("progression_loss_shield", "Block lose screen", _lossShield);
            if (gm == null) return;
            GUILayout.Label("Current: " + gm.currentQuota + "   Required: " + gm.NetworkrequiredQuotaToNextFloor);
            var currentText = EditText("current_quota", "Set current quota", gm.currentQuota.ToString());
            BigNumber current;
            if (TryParseBigNumber(currentText, out current) && current >= BigNumber.One)
                Button("Apply current quota", () => gm.currentQuota = current);
            Button("Complete current quota", CompleteCurrentQuota);
            var req = EditText("required_quota", "Required quota", gm.NetworkrequiredQuotaToNextFloor.ToString(CultureInfo.InvariantCulture));
            long parsed;
            if (long.TryParse(req, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
                Button("Apply required quota", () => gm.NetworkrequiredQuotaToNextFloor = parsed);
        }

        private void DrawAdvancedTab()
        {
            GUILayout.Label("Advanced rules");
            RegisterToggle("progression_persave", "Per-save rules", _perSaveRules);
            var npcText = EditText("npc_count", "NPC crowd (-1=vanilla)", _npcCount.Value.ToString(CultureInfo.InvariantCulture));
            int npc;
            if (int.TryParse(npcText, out npc)) _npcCount.Value = npc;
            var daysText = EditText("days_per_quota", "Days per quota (-1=vanilla)", _daysBeforeQuota.Value.ToString(CultureInfo.InvariantCulture));
            int days;
            if (int.TryParse(daysText, out days)) _daysBeforeQuota.Value = days;
            var ticketsText = EditText("daily_ticket_reward", "Daily ticket reward (-1=vanilla)", _dailyTicketReward.Value.ToString(CultureInfo.InvariantCulture));
            int dailyTickets;
            if (int.TryParse(ticketsText, out dailyTickets)) _dailyTicketReward.Value = dailyTickets;
            Slider("Catch-up factor", _catchUpFactor, -1f, 10f);
            var startingMoney = EditText("starting_money", "Starting money (-1=vanilla)", _startingMoney.Value.ToString(CultureInfo.InvariantCulture));
            long moneyValue;
            if (long.TryParse(startingMoney, NumberStyles.Integer, CultureInfo.InvariantCulture, out moneyValue)) _startingMoney.Value = moneyValue;
            var startingTickets = EditText("starting_tickets", "Starting tickets (-1=vanilla)", _startingTickets.Value.ToString(CultureInfo.InvariantCulture));
            long ticketValue;
            if (long.TryParse(startingTickets, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticketValue)) _startingTickets.Value = ticketValue;
            var startingQuota = EditText("starting_quota", "Starting quota (-1=vanilla)", _startingQuota.Value.ToString(CultureInfo.InvariantCulture));
            long quotaValue;
            if (long.TryParse(startingQuota, NumberStyles.Integer, CultureInfo.InvariantCulture, out quotaValue)) _startingQuota.Value = quotaValue;
            Button("Apply rules", () => RunHost(ApplyAdvancedRules));
            Button("Restore vanilla rules", RestoreAdvancedConfig);
            RegisterToggle("progression_challenge_tools", "Challenge tools", _challengeTools);
            if (!_challengeTools.Value) return;
            var cm = ChallengeManager.Instance;
            if (cm == null) return;
            GUILayout.Label("Challenges");
            var active = cm.GetActiveChallenges();
            if (active != null)
            {
                _challengeScroll = GUILayout.BeginScrollView(_challengeScroll, GUILayout.Height(120));
                foreach (var p in active)
                {
                    if (p == null) continue;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label((p.challenge == null ? "?" : p.challenge.challengeName) + " " + p.progress.ToString("0%") + (p.isClaimed ? " (claimed)" : ""));
                    if (p.isCompleted && !p.isClaimed && p.challenge != null) Button("Claim", () => cm.ClaimChallengeReward(p.challenge));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.BeginHorizontal();
            Button("Complete all and claim", () =>
            {
                cm.ServerCompleteAllActiveChallenges();
                var completed = cm.GetActiveChallenges();
                if (completed != null) foreach (var p in completed) if (p != null && p.challenge != null && p.isCompleted && !p.isClaimed) cm.ClaimChallengeReward(p.challenge);
            });
            GUILayout.EndHorizontal();
            var floor = EditText("challenge_floor", "Challenge floor (-1=all)", "-1");
            int floorNo;
            if (!int.TryParse(floor, NumberStyles.Integer, CultureInfo.InvariantCulture, out floorNo) || floorNo < -1) return;
            var search = EditText("challenge_search", "Search challenge name / ID", "");
            var settings = Resources.Load<ChallengeSettings>("ChallengeSettings");
            var catalog = floorNo >= 0 ? cm.GetChallengesByFloorIndex(floorNo) : settings == null ? null : settings.challenges;
            if (catalog == null) return;
            var matches = new List<Challenge>();
            foreach (var challenge in catalog)
            {
                if (challenge == null) continue;
                if (!string.IsNullOrEmpty(search) && (challenge.challengeName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    challenge.challengeID.ToString(CultureInfo.InvariantCulture).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(challenge);
            }
            if (matches.Count > 0) Button("Activate random matching challenge", () =>
            {
                var eligible = matches.FindAll(challenge => !cm.IsChallengeActive(challenge));
                if (eligible.Count == 0) { SetStatus("All matching challenges are active"); return; }
                cm.ServerActivateChallengeById(eligible[UnityEngine.Random.Range(0, eligible.Count)].challengeID);
            });
            GUILayout.Label("Matching challenges: " + matches.Count);
            _challengeCatalogScroll = GUILayout.BeginScrollView(_challengeCatalogScroll, GUILayout.Height(180));
            foreach (var challenge in matches)
            {
                var selected = challenge;
                GUILayout.BeginHorizontal();
                GUILayout.Label("#" + selected.challengeID + " " + selected.challengeName + " (floor " + selected.floorIndex + ")");
                if (cm.IsChallengeActive(selected)) GUILayout.Label("Active", GUILayout.Width(70));
                else Button("Activate", () => cm.ServerActivateChallengeById(selected.challengeID));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void RestoreAdvancedConfig()
        {
            _npcCount.Value = -1; _daysBeforeQuota.Value = -1; _dailyTicketReward.Value = -1; _catchUpFactor.Value = -1f;
            _startingMoney.Value = -1L; _startingTickets.Value = -1L; _startingQuota.Value = -1L;
            ClearProgressionEditBuffers();
            RestoreVanillaRules();
            SaveRulesForCurrentSave(_lastProgressionManager);
        }

        private void MeetQuotaMoney()
        {
            var gm = Find<GameManager>();
            var mm = MoneyManager.Instance;
            if (gm == null || mm == null) return;
            RunHost(() =>
            {
                var need = gm.currentQuota - mm.balance;
                if (need > BigNumber.Zero) mm.TryChangeBalance(need, LocalProfile(), ChangeType.Misc);
            });
        }

        private void CompleteCurrentQuota()
        {
            var gm = Find<GameManager>();
            if (gm != null) RunHost(() => InvokeGame(gm, "ProgressNextQuota"));
        }

        private void ProgressFloor()
        {
            var gm = Find<GameManager>();
            if (gm != null) RunHost(() => InvokeGame(gm, "ProgressFloor"));
        }

        private void EndCurrentDay()
        {
            var gm = Find<GameManager>();
            if (gm != null) RunHost(() => gm.ServerAdjustTimer(-gm.RoundTimerRemaining - 1f));
        }

        private void WorldReloadSave(SaveManager save)
        {
            RunHost(() => ReloadCurrentSaveData(save, false));
        }

        private void WorldResetCurrentSave(SaveManager save)
        {
            RunHost(() => ReloadCurrentSaveData(save, true));
        }

        private void ReloadCurrentSaveData(SaveManager save, bool reset)
        {
            if (!NetworkServer.active) throw new InvalidOperationException("Нужен активный host/server");
            if (save == null || save != SaveManager.Instance || string.IsNullOrEmpty(save.CurrentSaveName))
                throw new InvalidOperationException("Текущее сохранение не выбрано");
            var gm = GameManager.Instance;
            var challenges = ChallengeManager.Instance;
            if (gm == null || MoneyManager.Instance == null || ItemManager.Instance == null || challenges == null)
                throw new InvalidOperationException("Менеджеры игры ещё не готовы");
            if ((bool)GetField(gm, "_isTransitioning")) throw new InvalidOperationException("Дождитесь перехода сцены");
            if (Resources.Load<GameSettings>("GameSettings") == null || Resources.Load<ChallengeSettings>("ChallengeSettings") == null)
                throw new InvalidOperationException("GameSettings недоступен");

            var previousData = GetField(save, "currentSaveData") as SaveData;
            var previousLoaded = (bool)GetField(save, "hasLoadedGame");
            // The native startup coroutine prefers a cached PlayerPrefs JSON blob.
            // Read the current slot through its native disk loader instead.
            var data = reset ? null : InvokeGame(save, "LoadSaveDataFromFile", save.CurrentSaveName) as SaveData;
            if (!reset && (data == null || !string.Equals(data.saveName, save.CurrentSaveName, StringComparison.Ordinal) || data.itemIds == null))
                throw new InvalidOperationException("Не удалось прочитать текущее сохранение с диска; состояние не изменено");
            var previousRulesLoaded = _rulesLoadedForSave;
            var previousQuotaInitialized = _liveQuotaInitialized;
            var previousQuotaMultiplier = _liveQuotaMultiplier;
            var previousDurationApplied = _dayDurationApplied;
            var previousDurationBeforeOverride = _dayDurationBeforeOverride;
            var previousDuration = gm.Network_dayDuration;
            var previousBailoutDay = _bailoutDay;
            var previousSavedBailoutDay = _saveBailoutDay == null ? int.MinValue : _saveBailoutDay.Value;
            var previousSavedMultiplier = _saveAppliedQuotaMultiplier == null ? -1f : _saveAppliedQuotaMultiplier.Value;
            var previousSavedFingerprint = _saveQuotaFingerprint == null ? "" : _saveQuotaFingerprint.Value;
            try
            {
                if (reset)
                {
                    // Native Reset writes fresh save data but does not apply it to
                    // the live managers. LoadGame below performs that missing step.
                    save.ResetCurrentSaveToDefaults();
                    data = GetField(save, "currentSaveData") as SaveData;
                    if (data == null || ReferenceEquals(data, previousData) || data.itemIds == null)
                        throw new InvalidOperationException("Штатный сброс сохранения не выполнен");
                }
                else SetField(save, "currentSaveData", data);
                SetField(save, "hasLoadedGame", false);
                // RestoreChallenge adds/updates entries without removing challenges
                // absent from the save. Clear them with the native server method.
                challenges.ServerResetAllChallenges();
                save.LoadGame();
                if (!(bool)GetField(save, "hasLoadedGame"))
                    throw new InvalidOperationException("Штатная загрузка не применила сохранение");
                var random = SeededRandomManager.Instance;
                if (!reset && random != null)
                {
                    if (data.seed == 0) data.seed = SaveGameRules.ResolveWorldSeed(0);
                    random.InitializeSeed(data.seed);
                }
                ClearProgressionEditBuffers();
                SetStatus(reset ? "Сохранение сброшено и применено; физический мир обновится при смене сцены" : "Сохранение прочитано с диска и применено; физический мир обновится при смене сцены");
            }
            catch
            {
                SetField(save, "currentSaveData", previousData);
                SetField(save, "hasLoadedGame", previousLoaded);
                _rulesLoadedForSave = previousRulesLoaded;
                _liveQuotaInitialized = previousQuotaInitialized;
                _liveQuotaMultiplier = previousQuotaMultiplier;
                _dayDurationApplied = previousDurationApplied;
                _dayDurationBeforeOverride = previousDurationBeforeOverride;
                gm.Network_dayDuration = previousDuration;
                _bailoutDay = previousBailoutDay;
                _bailoutUsedToday = _bailoutDay == gm.NetworkdaysPassed;
                if (_saveBailoutDay != null) _saveBailoutDay.Value = previousSavedBailoutDay;
                if (_saveAppliedQuotaMultiplier != null) _saveAppliedQuotaMultiplier.Value = previousSavedMultiplier;
                if (_saveQuotaFingerprint != null) _saveQuotaFingerprint.Value = previousSavedFingerprint;
                throw;
            }
        }

        private static bool TryParseBigNumber(string value, out BigNumber number)
        {
            try { number = BigNumber.Parse(value); return !double.IsNaN(number.Mantissa) && !double.IsInfinity(number.Mantissa); }
            catch { number = BigNumber.Zero; return false; }
        }

        private void LoadRulesForCurrentSave(GameManager gm)
        {
            if (!_perSaveRules.Value) return;
            if (_rulesLoadedForSave) return;
            var sm = SaveManager.Instance;
            if (sm == null || string.IsNullOrEmpty(sm.CurrentSaveName)) return;
            var section = "SaveRules." + SanitizeSection(sm.CurrentSaveName);
            // Binding existing entries is enough for a sidecar config section and avoids touching game saves.
            _saveDayLength = Config.Bind(section, "DayLengthSeconds", _dayLengthSeconds.Value);
            _saveQuotaMultiplier = Config.Bind(section, "QuotaMultiplier", _quotaMultiplier.Value);
            _saveOverrideDayLength = Config.Bind(section, "OverrideDayLength", _overrideDayLength.Value);
            _saveOverrideQuota = Config.Bind(section, "OverrideQuota", _overrideQuota.Value);
            _saveNpcCount = Config.Bind(section, "NpcCount", _npcCount.Value);
            _saveDaysBeforeQuota = Config.Bind(section, "DaysBeforeQuota", _daysBeforeQuota.Value);
            _saveDailyTickets = Config.Bind(section, "DailyTicketReward", _dailyTicketReward.Value);
            _saveCatchUp = Config.Bind(section, "CatchUpFactor", _catchUpFactor.Value);
            _saveInfiniteDays = Config.Bind(section, "InfiniteDays", _infiniteDays.Value);
            _saveAutoMoney = Config.Bind(section, "AutoMoney", _autoMoney.Value);
            _saveAutoMoneyAmount = Config.Bind(section, "AutoMoneyAmount", _autoMoneyAmount.Value);
            _saveAutoBailout = Config.Bind(section, "AutoBailout", _autoBailout.Value);
            _saveBailoutPercent = Config.Bind(section, "BailoutPercent", _bailoutPercent.Value);
            _saveLossShield = Config.Bind(section, "LossShield", _lossShield.Value);
            _saveChallengeTools = Config.Bind(section, "ChallengeTools", _challengeTools.Value);
            _saveTimerDelta = Config.Bind(section, "DayTimerDelta", DayTimerDelta.Value);
            _saveStartingMoney = Config.Bind(section, "StartingMoney", _startingMoney.Value);
            _saveStartingTickets = Config.Bind(section, "StartingTickets", _startingTickets.Value);
            _saveStartingQuota = Config.Bind(section, "StartingQuota", _startingQuota.Value);
            _savePauseDayTimer = Config.Bind(section, "PauseDayTimer", PauseDayTimer.Value);
            _saveGameSpeed = Config.Bind(section, "GameSpeed", GameSpeed.Value);
            _saveNoMoneySpend = Config.Bind(section, "NoMoneySpend", NoMoneySpend.Value);
            _saveNoTicketSpend = Config.Bind(section, "NoTicketSpend", NoTicketSpend.Value);
            _saveMoneyGainMultiplier = Config.Bind(section, "MoneyGainMultiplier", MoneyGainMultiplier.Value);
            _dayLengthSeconds.Value = _saveDayLength.Value;
            _quotaMultiplier.Value = _saveQuotaMultiplier.Value;
            _overrideDayLength.Value = _saveOverrideDayLength.Value;
            _overrideQuota.Value = _saveOverrideQuota.Value;
            _npcCount.Value = _saveNpcCount.Value;
            _daysBeforeQuota.Value = _saveDaysBeforeQuota.Value;
            _dailyTicketReward.Value = _saveDailyTickets.Value;
            _catchUpFactor.Value = _saveCatchUp.Value;
            _infiniteDays.Value = _saveInfiniteDays.Value;
            _autoMoney.Value = _saveAutoMoney.Value;
            _autoMoneyAmount.Value = _saveAutoMoneyAmount.Value;
            _autoBailout.Value = _saveAutoBailout.Value;
            _bailoutPercent.Value = _saveBailoutPercent.Value;
            _lossShield.Value = _saveLossShield.Value;
            _challengeTools.Value = _saveChallengeTools.Value;
            DayTimerDelta.Value = _saveTimerDelta.Value;
            _startingMoney.Value = _saveStartingMoney.Value;
            _startingTickets.Value = _saveStartingTickets.Value;
            _startingQuota.Value = _saveStartingQuota.Value;
            PauseDayTimer.Value = _savePauseDayTimer.Value;
            GameSpeed.Value = _saveGameSpeed.Value;
            NoMoneySpend.Value = _saveNoMoneySpend.Value;
            NoTicketSpend.Value = _saveNoTicketSpend.Value;
            MoneyGainMultiplier.Value = _saveMoneyGainMultiplier.Value;
            _rulesLoadedForSave = true;
            // The saved quota already includes its multiplier. Only later UI changes
            // should rescale it, otherwise each load would multiply it again.
            _liveQuotaInitialized = false;
            ApplyLiveQuotaMultiplier(gm);
            ApplyAdvancedRules();
        }

        private void SaveRulesForCurrentSave(GameManager gm)
        {
            if (!_rulesLoadedForSave || !_previousPerSaveEnabled || string.IsNullOrEmpty(_loadedSaveName)) return;
            // Cached entries avoid repeated binding in the frame loop.
            if (_saveDayLength != null) _saveDayLength.Value = _dayLengthSeconds.Value;
            if (_saveQuotaMultiplier != null) _saveQuotaMultiplier.Value = _quotaMultiplier.Value;
            if (_saveOverrideDayLength != null) _saveOverrideDayLength.Value = _overrideDayLength.Value;
            if (_saveOverrideQuota != null) _saveOverrideQuota.Value = _overrideQuota.Value;
            if (_saveNpcCount != null) _saveNpcCount.Value = _npcCount.Value;
            if (_saveDaysBeforeQuota != null) _saveDaysBeforeQuota.Value = _daysBeforeQuota.Value;
            if (_saveDailyTickets != null) _saveDailyTickets.Value = _dailyTicketReward.Value;
            if (_saveCatchUp != null) _saveCatchUp.Value = _catchUpFactor.Value;
            if (_saveInfiniteDays != null) _saveInfiniteDays.Value = _infiniteDays.Value;
            if (_saveAutoMoney != null) _saveAutoMoney.Value = _autoMoney.Value;
            if (_saveAutoMoneyAmount != null) _saveAutoMoneyAmount.Value = _autoMoneyAmount.Value;
            if (_saveAutoBailout != null) _saveAutoBailout.Value = _autoBailout.Value;
            if (_saveBailoutPercent != null) _saveBailoutPercent.Value = _bailoutPercent.Value;
            if (_saveLossShield != null) _saveLossShield.Value = _lossShield.Value;
            if (_saveChallengeTools != null) _saveChallengeTools.Value = _challengeTools.Value;
            if (_saveTimerDelta != null) _saveTimerDelta.Value = DayTimerDelta.Value;
            if (_saveStartingMoney != null) _saveStartingMoney.Value = _startingMoney.Value;
            if (_saveStartingTickets != null) _saveStartingTickets.Value = _startingTickets.Value;
            if (_saveStartingQuota != null) _saveStartingQuota.Value = _startingQuota.Value;
            if (_savePauseDayTimer != null) _savePauseDayTimer.Value = PauseDayTimer.Value;
            if (_saveGameSpeed != null) _saveGameSpeed.Value = GameSpeed.Value;
            if (_saveNoMoneySpend != null) _saveNoMoneySpend.Value = NoMoneySpend.Value;
            if (_saveNoTicketSpend != null) _saveNoTicketSpend.Value = NoTicketSpend.Value;
            if (_saveMoneyGainMultiplier != null) _saveMoneyGainMultiplier.Value = MoneyGainMultiplier.Value;
        }

        private static string SanitizeSection(string value)
        {
            var section = new System.Text.StringBuilder();
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '_' || character == '-') section.Append(character);
                else section.Append('%').Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
            return section.ToString();
        }

        private static bool ProgressionPatchActive()
        {
            return NetworkServer.active && Enabled != null && Enabled.Value && Instance != null && Instance._progressionEnabled != null && Instance._progressionEnabled.Value;
        }

        // Scale generated values rather than synchronized setters: direct edits and restored
        // saves must not be multiplied again.
        [HarmonyPatch(typeof(GameSettings), nameof(GameSettings.GetStartingQuota))]
        private static class StartingQuotaScalePatch
        {
            private static void Postfix(ref BigNumber __result)
            {
                if (!ProgressionPatchActive() || !Instance._overrideQuota.Value) return;
                __result = BigNumber.Max(BigNumber.One, __result * (double)Instance._quotaMultiplier.Value);
            }
        }

        [HarmonyPatch(typeof(GameSettings), nameof(GameSettings.GetQuota))]
        private static class GeneratedQuotaScalePatch
        {
            private static void Prefix(int __0, ref BigNumber __1)
            {
                if (__0 <= 0 || !ProgressionPatchActive() || !Instance._overrideQuota.Value) return;
                var multiplier = Instance._quotaMultiplier.Value;
                if (multiplier > 0f) __1 /= (double)multiplier;
            }

            private static void Postfix(int __0, ref BigNumber __result)
            {
                // GetQuota(0, ...) calls GetStartingQuota and is already scaled there.
                if (__0 <= 0 || !ProgressionPatchActive() || !Instance._overrideQuota.Value) return;
                __result = BigNumber.Max(BigNumber.One, __result * (double)Instance._quotaMultiplier.Value);
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ServerSetScene))]
        private static class ProgressionLossShieldPatch
        {
            private static bool Prefix(GameState __0)
            {
                return __0 != GameState.Lose || !ProgressionPatchActive() || !Instance._lossShield.Value;
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartDay))]
        private static class ProgressionDayStartPatch
        {
            private static void Postfix(GameManager __instance)
            {
                if (ProgressionPatchActive()) Instance._bailoutUsedToday = Instance._bailoutDay == __instance.NetworkdaysPassed;
            }
        }

        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ApplySaveRules))]
        private static class ProgressionApplySaveRulesPatch
        {
            private static void Postfix(GameManager __instance)
            {
                if (!ProgressionPatchActive()) return;
                Instance._dayDurationApplied = false;
                Instance._dayDurationBeforeOverride = __instance.Network_dayDuration;
            }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.LoadGame))]
        private static class ProgressionSaveLoadedPatch
        {
            private static void Prefix(bool ___hasLoadedGame, SaveData ___currentSaveData, out bool __state)
            {
                __state = ProgressionPatchActive() && !___hasLoadedGame && ___currentSaveData != null;
                if (!__state) return;
                var gm = Find<GameManager>();
                if (gm == null) return;
                Instance.DetectProgressionSaveChange(gm);
                Instance.SaveRulesForCurrentSave(gm);
                Instance.LoadRulesForCurrentSave(gm);
            }

            private static void Postfix(bool __state, bool ___hasLoadedGame)
            {
                if (!__state || !___hasLoadedGame || !ProgressionPatchActive()) return;
                var gm = Find<GameManager>();
                if (gm == null) return;
                // Rebind even when name/manager are unchanged: the same slot may have
                // been reset and loaded again without recreating either singleton.
                var savedMultiplier = Instance._saveAppliedQuotaMultiplier == null ? -1f : Instance._saveAppliedQuotaMultiplier.Value;
                var matchesSavedQuota = Instance._saveQuotaFingerprint != null && Instance._saveQuotaFingerprint.Value == QuotaFingerprint(gm.currentQuota, gm.NetworkdaysPassed, gm.NetworksuccessfulQuota);
                Instance._rulesLoadedForSave = false;
                Instance.LoadRulesForCurrentSave(gm);
                // Older profiles do not record whether their quota was already
                // scaled. Leave those values intact instead of multiplying twice.
                Instance._liveQuotaInitialized = matchesSavedQuota && savedMultiplier > 0f && !float.IsNaN(savedMultiplier) && !float.IsInfinity(savedMultiplier);
                if (Instance._liveQuotaInitialized) Instance._liveQuotaMultiplier = savedMultiplier;
                Instance.ApplyLiveQuotaMultiplier(gm);
                Instance.ApplyRuntimeDayLength(gm);
            }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.ResetCurrentSaveToDefaults))]
        private static class ProgressionSaveResetPatch
        {
            private static void Prefix(SaveData ___currentSaveData)
            {
                if (!ProgressionPatchActive()) return;
                var gm = Find<GameManager>();
                if (gm == null) return;
                Instance.DetectProgressionSaveChange(gm);
                Instance.SaveRulesForCurrentSave(gm);
                Instance.LoadRulesForCurrentSave(gm);
                // Reset retains SaveGameRules, whose starting balances take priority
                // over GameSettings in the game's native reset routine.
                if (___currentSaveData != null && ___currentSaveData.rules != null)
                {
                    if (Instance._startingMoney.Value >= 0) ___currentSaveData.rules.startingMoney = Instance._startingMoney.Value;
                    if (Instance._startingTickets.Value >= 0) ___currentSaveData.rules.startingTickets = Instance._startingTickets.Value;
                }
            }

            private static void Postfix(SaveManager __instance, SaveData ___currentSaveData)
            {
                if (!ProgressionPatchActive() || string.IsNullOrEmpty(__instance.CurrentSaveName)) return;
                Instance._bailoutDay = int.MinValue;
                Instance._bailoutUsedToday = false;
                if (Instance._saveBailoutDay != null) Instance._saveBailoutDay.Value = int.MinValue;
                if (Instance._saveAppliedQuotaMultiplier != null)
                    Instance._saveAppliedQuotaMultiplier.Value = Instance._overrideQuota.Value ? Mathf.Clamp(Instance._quotaMultiplier.Value, 0.01f, 100f) : 1f;
                if (Instance._saveQuotaFingerprint != null && ___currentSaveData != null)
                    Instance._saveQuotaFingerprint.Value = QuotaFingerprint(___currentSaveData.GetCurrentQuota(), ___currentSaveData.daysPassed, ___currentSaveData.successfulQuota);
                Instance._rulesLoadedForSave = false;
                Instance._liveQuotaInitialized = false;
            }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveGame))]
        private static class ProgressionSaveWritePatch
        {
            private static void Prefix()
            {
                if (!ProgressionPatchActive()) return;
                var gm = Find<GameManager>();
                if (gm == null) return;
                Instance.DetectProgressionSaveChange(gm);
                Instance.LoadRulesForCurrentSave(gm);
                Instance.ApplyLiveQuotaMultiplier(gm);
                Instance.SaveRulesForCurrentSave(gm);
                if (Instance._saveAppliedQuotaMultiplier != null) Instance._saveAppliedQuotaMultiplier.Value = Instance._liveQuotaMultiplier;
                if (Instance._saveQuotaFingerprint != null)
                    Instance._saveQuotaFingerprint.Value = QuotaFingerprint(gm.currentQuota, gm.NetworkdaysPassed, gm.NetworksuccessfulQuota);
            }
        }

    }
}
