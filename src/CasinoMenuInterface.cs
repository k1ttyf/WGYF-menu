using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace CasinoMenu
{
    // Menu, input and the local self controls live in their own partial so the
    // game-facing tabs do not need to know about Input System details.
    public sealed partial class CasinoMenuPlugin
    {
        private sealed class InterfaceAction
        {
            internal string Id;
            internal string Label;
            internal Action Fire;
            internal ConfigEntry<KeyCode> Binding;
            internal ConfigEntry<KeyboardShortcut> Shortcut;
            internal ConfigEntry<bool> Toggle;
        }

        private readonly Dictionary<string, InterfaceAction> _interfaceActions = new Dictionary<string, InterfaceAction>();
        private bool _interfaceDisabledCleanup;
        private string _rebindAction;
        private int _rebindStartFrame;
        private int _bindingCapturedFrame = -1;
        private bool _textInputFocused;
        private InputLayer _previousInputLayer;
        private bool _ownsInputLayer;
        private CursorLockMode _previousCursorLock;
        private bool _previousCursorVisible;
        private bool _cursorStateCaptured;
        private bool _nativeNoClip;
        private Noclip _nativeNoclip;
        private float _nativeOriginalSpeed;
        private float _nativeOriginalSprintSpeed;
        private bool _nativeSpeedCaptured;
        private bool _nativeControllerEnabled;
        private bool _nativeBodyKinematic;
        private bool _nativeWasLocked;
        private RigidbodyInterpolation _nativeBodyInterpolation;
        private Rigidbody _nativeBody;
        private PlayerController _nativeController;
        private readonly Dictionary<Collider, bool> _nativeColliderStates = new Dictionary<Collider, bool>();
        private ConfigEntry<bool> _interfaceLiveLog;
        private ConfigEntry<bool> _movementEnabled;
        private ConfigEntry<bool> _jumpEnabled;

        private void InitInterface()
        {
            _interfaceLiveLog = Config.Bind("Interface", "LiveStatusLog", false, "Write menu status changes to the log.");
            _movementEnabled = Config.Bind("Self", "MovementBoostEnabled", true, "Apply movement multiplier.");
            _jumpEnabled = Config.Bind("Self", "JumpBoostEnabled", true, "Apply jump multiplier.");

            RegisterShortcutAction("progression_add_money", "Add money", () => AddMoney(1000), AddMoneyKey);
            RegisterShortcutAction("self.remove_money", "Remove money", () => RemoveMoney(1000), RemoveMoneyKey);
            RegisterShortcutAction("self.add_tickets", "Add tickets", () => AddTickets(10), AddTicketsKey);
            RegisterShortcutAction("progression_meet_quota", "Meet quota", MeetQuota, MeetQuotaKey);
            RegisterShortcutAction("self.heal", "Heal organs", HealLocalPlayer, HealKey);
            RegisterShortcutAction("self.trigger_win", "Trigger nearby win", TriggerNearbyWin, TriggerWin);
            RegisterAction("self.trigger_loss", "Trigger nearby loss", TriggerNearbyLoss);
            RegisterShortcutAction("self.noclip", "Toggle noclip", () => SetNativeNoClip(!_nativeNoClip), NoClipToggle);
            RegisterShortcutAction("menu.toggle", "Toggle menu", () => SetMenuOpen(!_menuOpen), ToggleMenu);

            // Register every bool config entry up front.  Tabs can then add a
            // friendly label later without making keybind availability depend
            // on which tab was opened first.
            foreach (var entry in ((IDictionary<ConfigDefinition, ConfigEntryBase>)Config).Values.OfType<ConfigEntry<bool>>().ToList())
            {
                var definition = entry.Definition;
                if (definition.Section.StartsWith("GlobalRules.", StringComparison.Ordinal) ||
                    definition.Section.StartsWith("SaveRules.", StringComparison.Ordinal) ||
                    ReferenceEquals(entry, AutoUpdaterEnabled) || ReferenceEquals(entry, RequireHost)) continue;
                EnsureToggleBinding("config." + definition.Section + "." + definition.Key, definition.Key, entry);
            }
        }

        private void ShutdownInterface()
        {
            SetNativeNoClip(false);
            SetMenuOpen(false);
            _interfaceActions.Clear();
        }

        private void TickInterface()
        {
            if (_rebindAction == null && Time.frameCount != _bindingCapturedFrame)
                foreach (var action in _interfaceActions.Values.Where(x => x.Id == "menu.toggle"))
                    if (action.Shortcut != null && ShortcutDown(action.Shortcut.Value)) SetMenuOpen(!_menuOpen);
            foreach (var action in _interfaceActions.Values.Where(x => x.Toggle != null).ToList())
            {
                if (Time.frameCount == _bindingCapturedFrame || (_menuOpen && (_rebindAction != null || _textInputFocused))) continue;
                if (action.Binding != null && KeyDown(action.Binding.Value)) RunAction(action.Fire);
            }
            if (!Enabled.Value)
            {
                if (!_interfaceDisabledCleanup)
                {
                    SetNativeNoClip(false);
                    ShutdownWorld();
                    _interfaceDisabledCleanup = true;
                }
                if (_menuOpen) ClearMenuInput();
                return;
            }
            _interfaceDisabledCleanup = false;
            foreach (var action in _interfaceActions.Values.ToList())
            {
                if (action.Binding == null && action.Shortcut == null) continue;
                if (action.Id == "menu.toggle") continue;
                if (action.Toggle != null) continue;
                if (Time.frameCount == _bindingCapturedFrame || (_menuOpen && (_rebindAction != null || _textInputFocused))) continue;
                if ((action.Shortcut != null && ShortcutDown(action.Shortcut.Value)) ||
                    (action.Binding != null && KeyDown(action.Binding.Value))) RunAction(action.Fire);
            }
            if (_menuOpen)
            {
                if (_ownsInputLayer && InputEvents.ActiveLayer != InputLayer.UI) _ownsInputLayer = false;
                ClearMenuInput();
            }
            TickNativeNoClip();
        }

        private void SetMenuOpen(bool open)
        {
            if (_menuOpen == open) return;
            if (open) ReleaseGameplayButtons();
            _menuOpen = open;
            if (open)
            {
                _previousInputLayer = InputEvents.ActiveLayer;
                _ownsInputLayer = _previousInputLayer != InputLayer.UI;
                if (_ownsInputLayer) InputEvents.ActiveLayer = InputLayer.UI;
                _cursorStateCaptured = true;
                if (_cursorStateCaptured)
                {
                    _previousCursorLock = Cursor.lockState;
                    _previousCursorVisible = Cursor.visible;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                ClearMenuInput();
            }
            else
            {
                var stillMenuLayer = InputEvents.ActiveLayer == InputLayer.UI;
                var stillOwnLayer = _ownsInputLayer && stillMenuLayer;
                if (stillOwnLayer)
                    InputEvents.ActiveLayer = _previousInputLayer;
                _ownsInputLayer = false;
                if (_cursorStateCaptured && stillMenuLayer)
                {
                    Cursor.lockState = _previousCursorLock;
                    Cursor.visible = _previousCursorVisible;
                }
                _cursorStateCaptured = false;
                _rebindAction = null;
                _textInputFocused = false;
            }
        }

        private void ClearMenuInput()
        {
            var pc = LocalController();
            if (pc == null) return;
            SetField(pc, "_moveInput", Vector2.zero);
            if (pc.head != null) SetField(pc.head, "_lookInput", Vector2.zero);
            var native = pc.GetComponentInChildren<Noclip>(true);
            if (native != null) SetField(native, "_horizontalInput", Vector2.zero);
        }

        private static void ReleaseGameplayButtons()
        {
            if (InputEvents.IsJumpPressed) InputEvents.UpdateJump(false);
            if (InputEvents.IsCrouchPressed) InputEvents.UpdateCrouch(false);
            if (InputEvents.IsSprintPressed) InputEvents.UpdateSprint(false);
            if (InputEvents.IsInteractPressed) InputEvents.UpdateInteract(false);
            if (InputEvents.IsUseItemPressed) InputEvents.UpdateUseItem(false);
            if (InputEvents.IsThrowItemPressed) InputEvents.UpdateThrowItem(false);
            if (InputEvents.IsZoomPressed) InputEvents.UpdateZoom(false);
        }

        private void RegisterAction(string id, string label, Action action)
        {
            InterfaceAction entry;
            if (!_interfaceActions.TryGetValue(id, out entry))
            {
                entry = new InterfaceAction { Id = id };
                _interfaceActions.Add(id, entry);
            }
            entry.Label = label;
            entry.Fire = action;
            if (entry.Binding == null)
                entry.Binding = Config.Bind("InterfaceKeybinds", "Key_" + id.Replace('.', '_'), DefaultKey(id), label);
        }

        private void RegisterShortcutAction(string id, string label, Action action, ConfigEntry<KeyboardShortcut> shortcut)
        {
            InterfaceAction entry;
            if (!_interfaceActions.TryGetValue(id, out entry))
            {
                entry = new InterfaceAction { Id = id };
                _interfaceActions.Add(id, entry);
            }
            entry.Label = label;
            entry.Fire = action;
            entry.Shortcut = shortcut;
            entry.Binding = null;
        }

        private void RegisterToggle(string id, string label, ConfigEntry<bool> entry)
        {
            if (entry == null) return;
            var action = _interfaceActions.Values.FirstOrDefault(x => ReferenceEquals(x.Toggle, entry));
            if (action == null)
            {
                var actionId = "config." + entry.Definition.Section + "." + entry.Definition.Key;
                action = new InterfaceAction { Id = actionId, Toggle = entry, Fire = () => entry.Value = !entry.Value };
                _interfaceActions[actionId] = action;
                action.Binding = Config.Bind("InterfaceKeybinds", "Key_" + actionId.Replace('.', '_'), KeyCode.None, label);
            }
            action.Label = label;
            GUILayout.BeginHorizontal();
            entry.Value = GUILayout.Toggle(entry.Value, label);
            GUILayout.Label(BindingName(action), GUILayout.Width(100));
            GUILayout.EndHorizontal();
        }

        private void EnsureToggleBinding(string id, string label, ConfigEntry<bool> entry)
        {
            InterfaceAction action;
            if (!_interfaceActions.TryGetValue(id, out action))
            {
                action = new InterfaceAction { Id = id, Label = label, Toggle = entry, Fire = () => entry.Value = !entry.Value };
                _interfaceActions.Add(id, action);
            }
            else
            {
                action.Label = label;
                action.Toggle = entry;
                action.Fire = () => entry.Value = !entry.Value;
            }
            if (action.Binding == null)
                action.Binding = Config.Bind("InterfaceKeybinds", "Key_" + id.Replace('.', '_'), KeyCode.None, label);
        }

        private void DrawSelfTab()
        {
            GUILayout.Label("Self");
            RegisterToggle("self.movement", "Movement boost", _movementEnabled);
            Slider("Movement multiplier", MovementMultiplier, 0.5f, 5f);
            RegisterToggle("self.jump", "Jump boost", _jumpEnabled);
            Slider("Jump multiplier", JumpMultiplier, 0.5f, 5f);
            Slider("Noclip speed multiplier", NoClipSpeed, 0.5f, 10f);
            Button(_nativeNoClip ? "Disable game noclip" : "Enable game noclip", () => SetNativeNoClip(!_nativeNoClip), false);
            GUILayout.Label("Noclip vertical: " + ShortcutName(NoClipUp.Value) + " / " + ShortcutName(NoClipDown.Value));
            GUILayout.BeginHorizontal();
            Button("Heal eyes", () => { HealOrgan(OrganType.LeftEye); HealOrgan(OrganType.RightEye); }, false);
            Button("Heal body", () => HealOrgan(OrganType.Body), false);
            Button("Heal mouth", () => HealOrgan(OrganType.Mouth), false);
            Button("Heal all", HealLocalPlayer, false);
            GUILayout.EndHorizontal();
            Button("Trigger win at nearest table", TriggerNearbyWin, true);
            Button("Trigger loss at nearest table", TriggerNearbyLoss, true);
        }

        private void DrawSettingsTab()
        {
            GUILayout.Label("Settings");
            Enabled.Value = GUILayout.Toggle(Enabled.Value, "Enabled");
            GUILayout.Label("Host/server active: " + Mirror.NetworkServer.active);
            _interfaceLiveLog.Value = GUILayout.Toggle(_interfaceLiveLog.Value, "Live status log");
            GUILayout.Label("Menu key: " + ShortcutName(ToggleMenu.Value));
            Button("Reset input layer", ForceResetInput, false);
            GUILayout.Label("Input layer: " + InputEvents.ActiveLayer + " | Menu owns layer: " + _ownsInputLayer);
            GUILayout.Label("Keyboard: " + (Keyboard.current == null ? "unavailable" : Keyboard.current.displayName) +
                " | Mouse: " + (Mouse.current == null ? "unavailable" : Mouse.current.displayName));
            var pressed = Keyboard.current == null ? "" : string.Join(" + ", Keyboard.current.allKeys
                .Where(key => key.isPressed).Select(key => key.displayName).Take(8).ToArray());
            GUILayout.Label("Pressed keys: " + (string.IsNullOrEmpty(pressed) ? "none" : pressed));
            GUILayout.Label("Noclip: " + _nativeNoClip + " | Cursor: " + Cursor.lockState + " | Text focus: " + _textInputFocused);
            GUILayout.Label("Status: " + _status);
            Slider("Game speed", GameSpeed, 0.25f, 5f);
        }

        private void DrawKeybindsTab()
        {
            GUILayout.Label("Keybinds");
            var query = EditText("interface.keybind.search", "Search", "").ToLowerInvariant();
            foreach (var action in _interfaceActions.Values.OrderBy(x => x.Label).Where(x => string.IsNullOrEmpty(query) || (x.Label ?? "").ToLowerInvariant().Contains(query)))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(action.Label, GUILayout.Width(250));
                GUILayout.Label(BindingName(action), GUILayout.Width(120));
                if (_rebindAction == action.Id)
                {
                    GUILayout.Label("Press a key...");
                    CaptureBinding(action);
                }
                else if (Button("Rebind", () => StartRebind(action.Id), false)) { }
                if (Button("Clear", () => { if (action.Shortcut != null) action.Shortcut.Value = KeyboardShortcut.Empty; else if (action.Binding != null) action.Binding.Value = KeyCode.None; }, false)) { }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Vertical noclip");
            DrawShortcutRebind("Noclip up", NoClipUp, "interface.noclip.up");
            DrawShortcutRebind("Noclip down", NoClipDown, "interface.noclip.down");
        }

        private void DrawShortcutRebind(string label, ConfigEntry<KeyboardShortcut> shortcut, string id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(250));
            GUILayout.Label(ShortcutName(shortcut.Value), GUILayout.Width(120));
            if (_rebindAction == id)
            {
                GUILayout.Label("Press a key...");
                var key = Time.frameCount > _rebindStartFrame ? PressedKey() : KeyCode.None;
                if (key == KeyCode.Escape) { _rebindAction = null; _bindingCapturedFrame = Time.frameCount; }
                else if (key != KeyCode.None) { shortcut.Value = CaptureShortcut(key); _rebindAction = null; _bindingCapturedFrame = Time.frameCount; }
            }
            else if (Button("Rebind", () => StartRebind(id), false)) { }
            GUILayout.EndHorizontal();
        }

        private void CaptureBinding(InterfaceAction action)
        {
            var key = Time.frameCount > _rebindStartFrame ? PressedKey(action.Shortcut != null) : KeyCode.None;
            if (key == KeyCode.Escape) { _rebindAction = null; _bindingCapturedFrame = Time.frameCount; return; }
            if (key != KeyCode.None)
            {
                if (action.Shortcut != null) action.Shortcut.Value = CaptureShortcut(key);
                else if (action.Binding != null) action.Binding.Value = key;
                _rebindAction = null;
                _bindingCapturedFrame = Time.frameCount;
            }
        }

        private void StartRebind(string id)
        {
            _rebindAction = id;
            _rebindStartFrame = Time.frameCount;
            GUI.FocusControl(null);
        }

        private static string BindingName(InterfaceAction action)
        {
            if (action.Shortcut != null) return ShortcutName(action.Shortcut.Value);
            return action.Binding == null || action.Binding.Value == KeyCode.None ? "[unbound]" : action.Binding.Value.ToString();
        }

        private void TickNativeNoClip()
        {
            if (!_nativeNoClip) return;
            if (_nativeNoclip == null || _nativeController != LocalController())
            {
                _nativeNoClip = _noClip = false;
                RestoreNativeNoClip();
                return;
            }
            var activeValue = GetField(_nativeNoclip, "_isNoclipActive");
            if (!(activeValue is bool) || !(bool)activeValue)
            {
                _nativeNoClip = _noClip = false;
                RestoreNativeNoClip();
                return;
            }
            var speed = GetNoClipBaseSpeed(_nativeController) * Mathf.Clamp(NoClipSpeed.Value, 0.5f, 10f);
            if (_nativeBody != null) _nativeBody.isKinematic = true;
            SetField(_nativeNoclip, "noclipSpeed", speed);
            SetField(_nativeNoclip, "noclipSprintSpeed", speed);
        }

        private void SetNativeNoClip(bool enabled)
        {
            if (!enabled && !_nativeSpeedCaptured && !_nativeNoClip) { _noClip = false; return; }
            var pc = LocalController();
            var native = pc == null ? null : pc.GetComponentInChildren<Noclip>(true);
            if (enabled && native == null) { SetStatus("Native noclip component not found"); return; }
            if (enabled)
            {
                if (!_nativeSpeedCaptured || _nativeNoclip != native)
                {
                    if (_nativeSpeedCaptured) RestoreNativeNoClip();
                    // Normalize a noclip enabled by the game's console before
                    // taking a baseline of its ordinary movement state.
                    if ((bool)GetField(native, "_isNoclipActive")) InvokeGame(native, "SetNoclipActive", false);
                    _nativeNoclip = native;
                    _nativeOriginalSpeed = (float)GetField(native, "noclipSpeed");
                    _nativeOriginalSprintSpeed = (float)GetField(native, "noclipSprintSpeed");
                    _nativeController = pc;
                    _nativeControllerEnabled = pc.enabled;
                    _nativeWasLocked = pc.IsLocked;
                    _nativeBody = pc.GetComponent<Rigidbody>();
                    if (_nativeBody != null) { _nativeBodyKinematic = _nativeBody.isKinematic; _nativeBodyInterpolation = _nativeBody.interpolation; }
                    _nativeColliderStates.Clear();
                    foreach (var collider in native.GetComponentsInChildren<Collider>(true)) _nativeColliderStates[collider] = collider.enabled;
                    _nativeSpeedCaptured = true;
                }
                InvokeGame(native, "SetNoclipActive", true);
                InvokeGame(native, "SetCollidersActive", false);
                _nativeNoClip = true;
                TickNativeNoClip();
            }
            else
            {
                _nativeNoClip = false;
                RestoreNativeNoClip();
            }
            _noClip = _nativeNoClip;
            SetStatus("Noclip: " + (_nativeNoClip ? "ON" : "OFF"));
        }

        private void RestoreNativeNoClip()
        {
            if (_nativeSpeedCaptured && _nativeNoclip != null)
            {
                InvokeGame(_nativeNoclip, "SetNoclipActive", false);
                SetField(_nativeNoclip, "noclipSpeed", _nativeOriginalSpeed);
                SetField(_nativeNoclip, "noclipSprintSpeed", _nativeOriginalSprintSpeed);
                foreach (var pair in _nativeColliderStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
                if (_nativeController != null) _nativeController.enabled = _nativeControllerEnabled;
                if (_nativeBody != null)
                {
                    _nativeBody.isKinematic = (_nativeBodyKinematic && !_nativeWasLocked) || (_nativeController != null && _nativeController.IsLocked);
                    _nativeBody.interpolation = _nativeBodyInterpolation;
                }
            }
            _nativeColliderStates.Clear();
            _nativeSpeedCaptured = false;
            _nativeNoclip = null;
            _nativeBody = null;
            _nativeController = null;
        }

        private static KeyCode DefaultKey(string id)
        {
            if (id == "menu.toggle") return KeyCode.Insert;
            if (id.EndsWith("trigger_win", StringComparison.Ordinal)) return KeyCode.F6;
            if (id.EndsWith("add_money", StringComparison.Ordinal)) return KeyCode.F7;
            if (id.EndsWith("remove_money", StringComparison.Ordinal)) return KeyCode.F8;
            if (id.EndsWith("add_tickets", StringComparison.Ordinal)) return KeyCode.F9;
            if (id.EndsWith("meet_quota", StringComparison.Ordinal)) return KeyCode.F10;
            if (id.EndsWith("heal", StringComparison.Ordinal)) return KeyCode.F11;
            if (id.EndsWith("noclip", StringComparison.Ordinal)) return KeyCode.F12;
            return KeyCode.None;
        }

        private void ForceResetInput()
        {
            var reset = typeof(InputEvents).GetMethod("ResetInputLayers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (reset != null) reset.Invoke(null, null);
            else InputEvents.ActiveLayer = InputLayer.Default;
            _ownsInputLayer = false;
            _menuOpen = false;
            _rebindAction = null;
            _textInputFocused = false;
            _cursorStateCaptured = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            ReleaseGameplayButtons();
            ClearMenuInput();
            GUI.FocusControl(null);
        }

        private static bool ShortcutDown(KeyboardShortcut shortcut)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !KeyDown(shortcut.MainKey, true)) return false;
            foreach (var modifier in shortcut.Modifiers) if (!KeyDown(modifier, false)) return false;
            return true;
        }

        private static bool KeyDown(KeyCode code, bool edge = true)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            var key = ToInputKey(code);
            if (key == Key.None) return false;
            var control = keyboard[key];
            return control != null && (edge ? control.wasPressedThisFrame : control.isPressed);
        }

        private static bool ShortcutPressed(KeyboardShortcut shortcut)
        {
            if (!KeyDown(shortcut.MainKey, false)) return false;
            foreach (var modifier in shortcut.Modifiers) if (!KeyDown(modifier, false)) return false;
            return true;
        }

        private static string ShortcutName(KeyboardShortcut shortcut)
        {
            var modifiers = shortcut.Modifiers == null ? "" : string.Join("+", shortcut.Modifiers.Select(x => x.ToString()).ToArray());
            return string.IsNullOrEmpty(modifiers) ? shortcut.MainKey.ToString() : modifiers + "+" + shortcut.MainKey;
        }

        private static bool IsModifier(KeyCode key)
        {
            return key == KeyCode.LeftControl || key == KeyCode.RightControl || key == KeyCode.LeftShift ||
                key == KeyCode.RightShift || key == KeyCode.LeftAlt || key == KeyCode.RightAlt;
        }

        private static KeyCode PressedKey(bool shortcut = true)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return KeyCode.None;
            foreach (var key in keyboard.allKeys)
            {
                if (key == null || !key.wasPressedThisFrame) continue;
                var code = FromInputKey(key.keyCode);
                if (code != KeyCode.None && (!shortcut || !IsModifier(code))) return code;
            }
            // A modifier released on its own can still be bound as the main key.
            if (shortcut)
                foreach (var key in keyboard.allKeys)
                    if (key != null && key.wasReleasedThisFrame && IsModifier(FromInputKey(key.keyCode))) return FromInputKey(key.keyCode);
            return KeyCode.None;
        }

        private static KeyboardShortcut CaptureShortcut(KeyCode key)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return new KeyboardShortcut(key);
            var modifiers = new List<KeyCode>();
            if (keyboard.leftCtrlKey.isPressed) modifiers.Add(KeyCode.LeftControl);
            if (keyboard.rightCtrlKey.isPressed) modifiers.Add(KeyCode.RightControl);
            if (keyboard.leftShiftKey.isPressed) modifiers.Add(KeyCode.LeftShift);
            if (keyboard.rightShiftKey.isPressed) modifiers.Add(KeyCode.RightShift);
            if (keyboard.leftAltKey.isPressed) modifiers.Add(KeyCode.LeftAlt);
            if (keyboard.rightAltKey.isPressed) modifiers.Add(KeyCode.RightAlt);
            modifiers.Remove(key);
            return new KeyboardShortcut(key, modifiers.ToArray());
        }

        private static Key ToInputKey(KeyCode code)
        {
            if (code >= KeyCode.A && code <= KeyCode.Z) return (Key)((int)Key.A + (int)code - (int)KeyCode.A);
            if (code >= KeyCode.F1 && code <= KeyCode.F12) return (Key)((int)Key.F1 + (int)code - (int)KeyCode.F1);
            if (code == KeyCode.Alpha0) return Key.Digit0;
            if (code >= KeyCode.Alpha1 && code <= KeyCode.Alpha9) return (Key)((int)Key.Digit1 + (int)code - (int)KeyCode.Alpha1);
            if (code >= KeyCode.Keypad0 && code <= KeyCode.Keypad9) return (Key)((int)Key.Numpad0 + (int)code - (int)KeyCode.Keypad0);
            switch (code)
            {
                case KeyCode.Space: return Key.Space; case KeyCode.Return: return Key.Enter; case KeyCode.Tab: return Key.Tab;
                case KeyCode.LeftControl: return Key.LeftCtrl; case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftShift: return Key.LeftShift; case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftAlt: return Key.LeftAlt; case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.Escape: return Key.Escape; case KeyCode.Insert: return Key.Insert; case KeyCode.Delete: return Key.Delete;
                case KeyCode.LeftArrow: return Key.LeftArrow; case KeyCode.RightArrow: return Key.RightArrow;
                case KeyCode.UpArrow: return Key.UpArrow; case KeyCode.DownArrow: return Key.DownArrow;
                case KeyCode.Backspace: return Key.Backspace; case KeyCode.Home: return Key.Home; case KeyCode.End: return Key.End;
                case KeyCode.PageUp: return Key.PageUp; case KeyCode.PageDown: return Key.PageDown;
                case KeyCode.CapsLock: return Key.CapsLock; case KeyCode.Numlock: return Key.NumLock;
                case KeyCode.ScrollLock: return Key.ScrollLock; case KeyCode.Pause: return Key.Pause;
                case KeyCode.Print: return Key.PrintScreen;
                case KeyCode.LeftCommand: case KeyCode.LeftWindows: return Key.LeftMeta;
                case KeyCode.RightCommand: case KeyCode.RightWindows: return Key.RightMeta;
                case KeyCode.KeypadEnter: return Key.NumpadEnter; case KeyCode.KeypadDivide: return Key.NumpadDivide;
                case KeyCode.KeypadMultiply: return Key.NumpadMultiply; case KeyCode.KeypadMinus: return Key.NumpadMinus;
                case KeyCode.KeypadPlus: return Key.NumpadPlus; case KeyCode.KeypadPeriod: return Key.NumpadPeriod;
                case KeyCode.Comma: return Key.Comma; case KeyCode.Period: return Key.Period; case KeyCode.Slash: return Key.Slash;
                case KeyCode.Backslash: return Key.Backslash; case KeyCode.Semicolon: return Key.Semicolon;
                case KeyCode.Quote: return Key.Quote; case KeyCode.LeftBracket: return Key.LeftBracket;
                case KeyCode.RightBracket: return Key.RightBracket; case KeyCode.Minus: return Key.Minus;
                case KeyCode.Equals: return Key.Equals; case KeyCode.BackQuote: return Key.Backquote;
                default: return Key.None;
            }
        }

        private static KeyCode FromInputKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return (KeyCode)((int)KeyCode.A + (int)key - (int)Key.A);
            if (key >= Key.F1 && key <= Key.F12) return (KeyCode)((int)KeyCode.F1 + (int)key - (int)Key.F1);
            if (key == Key.Digit0) return KeyCode.Alpha0;
            if (key >= Key.Digit1 && key <= Key.Digit9) return (KeyCode)((int)KeyCode.Alpha1 + (int)key - (int)Key.Digit1);
            if (key >= Key.Numpad0 && key <= Key.Numpad9) return (KeyCode)((int)KeyCode.Keypad0 + (int)key - (int)Key.Numpad0);
            switch (key)
            {
                case Key.Space: return KeyCode.Space; case Key.Enter: return KeyCode.Return; case Key.Tab: return KeyCode.Tab;
                case Key.LeftCtrl: return KeyCode.LeftControl; case Key.RightCtrl: return KeyCode.RightControl;
                case Key.LeftShift: return KeyCode.LeftShift; case Key.RightShift: return KeyCode.RightShift;
                case Key.LeftAlt: return KeyCode.LeftAlt; case Key.RightAlt: return KeyCode.RightAlt;
                case Key.Escape: return KeyCode.Escape; case Key.Insert: return KeyCode.Insert; case Key.Delete: return KeyCode.Delete;
                case Key.LeftArrow: return KeyCode.LeftArrow; case Key.RightArrow: return KeyCode.RightArrow;
                case Key.UpArrow: return KeyCode.UpArrow; case Key.DownArrow: return KeyCode.DownArrow;
                case Key.Backspace: return KeyCode.Backspace; case Key.Home: return KeyCode.Home; case Key.End: return KeyCode.End;
                case Key.PageUp: return KeyCode.PageUp; case Key.PageDown: return KeyCode.PageDown;
                case Key.CapsLock: return KeyCode.CapsLock; case Key.NumLock: return KeyCode.Numlock;
                case Key.ScrollLock: return KeyCode.ScrollLock; case Key.Pause: return KeyCode.Pause;
                case Key.PrintScreen: return KeyCode.Print;
                case Key.LeftMeta: return KeyCode.LeftWindows; case Key.RightMeta: return KeyCode.RightWindows;
                case Key.NumpadEnter: return KeyCode.KeypadEnter; case Key.NumpadDivide: return KeyCode.KeypadDivide;
                case Key.NumpadMultiply: return KeyCode.KeypadMultiply; case Key.NumpadMinus: return KeyCode.KeypadMinus;
                case Key.NumpadPlus: return KeyCode.KeypadPlus; case Key.NumpadPeriod: return KeyCode.KeypadPeriod;
                case Key.Comma: return KeyCode.Comma; case Key.Period: return KeyCode.Period; case Key.Slash: return KeyCode.Slash;
                case Key.Backslash: return KeyCode.Backslash; case Key.Semicolon: return KeyCode.Semicolon;
                case Key.Quote: return KeyCode.Quote; case Key.LeftBracket: return KeyCode.LeftBracket;
                case Key.RightBracket: return KeyCode.RightBracket; case Key.Minus: return KeyCode.Minus;
                case Key.Equals: return KeyCode.Equals; case Key.Backquote: return KeyCode.BackQuote;
                default: return KeyCode.None;
            }
        }

        [HarmonyPatch(typeof(Noclip), "Move")]
        private static class NativeNoclipMovementPatch
        {
            private static bool Prefix(Noclip __instance, Vector2 ____horizontalInput, Transform ____head)
            {
                if (Instance == null) return true;
                var player = __instance == null ? null : __instance.GetComponentInParent<PlayerController>();
                if (player == null || !player.isLocalPlayer) return true;
                if (Instance._menuOpen) return false;
                if (!Instance._nativeNoClip) return true;
                if (InputEvents.ActiveLayer != InputLayer.Default || ____head == null) return false;
                var lift = (ShortcutPressed(NoClipUp.Value) ? 1f : 0f) - (ShortcutPressed(NoClipDown.Value) ? 1f : 0f);
                var direction = Vector3.ProjectOnPlane(____head.forward, Vector3.up).normalized * ____horizontalInput.y +
                    Vector3.ProjectOnPlane(____head.right, Vector3.up).normalized * ____horizontalInput.x + Vector3.up * lift;
                if (direction.sqrMagnitude > 1f) direction.Normalize();
                // The native Move smooths from zero every frame, so its serialized
                // speed is not in world units. Keep native activation and input,
                // but integrate movement directly to preserve the x1 baseline.
                var speed = Instance.GetNoClipBaseSpeed(player) * Mathf.Clamp(NoClipSpeed.Value, 0.5f, 10f);
                __instance.transform.position += direction * speed * Time.unscaledDeltaTime;
                return false;
            }
        }

        [HarmonyPatch(typeof(InputReader), "OnEscapeMenu")]
        private static class MenuEscapeInputPatch
        {
            private static bool Prefix(InputAction.CallbackContext __0)
            {
                if (Instance == null || !Instance._menuOpen) return true;
                if (__0.performed && Instance._rebindAction == null)
                {
                    Instance._bindingCapturedFrame = Time.frameCount;
                    Instance.SetMenuOpen(false);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(PlayerController), "OnMove")]
        private static class MenuMovementInputPatch
        {
            private static bool Prefix(PlayerController __instance)
            {
                return Instance == null || !Instance._menuOpen || __instance == null || !__instance.isLocalPlayer;
            }
        }

        [HarmonyPatch(typeof(PlayerHead), "OnLook")]
        private static class MenuLookInputPatch
        {
            private static bool Prefix(PlayerHead __instance)
            {
                if (Instance == null || !Instance._menuOpen || __instance == null) return true;
                var controller = __instance.GetComponentInParent<PlayerController>();
                return controller == null || !controller.isLocalPlayer;
            }
        }

        [HarmonyPatch(typeof(PlayerHead), "GetInput")]
        private static class MenuHeadInputPatch
        {
            private static bool Prefix(PlayerHead __instance)
            {
                if (Instance == null || !Instance._menuOpen || __instance == null) return true;
                var controller = __instance.GetComponentInParent<PlayerController>();
                return controller == null || !controller.isLocalPlayer;
            }
        }

        [HarmonyPatch(typeof(PlayerController), "OnJump")]
        private static class MenuJumpInputPatch
        {
            private static bool Prefix(PlayerController __instance)
            {
                return Instance == null || !Instance._menuOpen || __instance == null || !__instance.isLocalPlayer;
            }
        }

        [HarmonyPatch(typeof(PlayerController), "OnCrouch")]
        private static class MenuCrouchInputPatch
        {
            private static bool Prefix(PlayerController __instance)
            {
                return Instance == null || !Instance._menuOpen || __instance == null || !__instance.isLocalPlayer;
            }
        }
    }
}
