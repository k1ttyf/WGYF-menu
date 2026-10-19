using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Mirror;
using UnityEngine;

namespace CasinoMenu
{
    public sealed partial class CasinoMenuPlugin
    {
        private static T Find<T>() where T : UnityEngine.Object
        {
            return UnityEngine.Object.FindFirstObjectByType<T>();
        }

        private static object GetField(object target, string name)
        {
            if (target == null) throw new InvalidOperationException("Game object is unavailable: " + name);
            var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
            if (field == null) throw new MissingFieldException(target.GetType().Name, name);
            return field.GetValue(target);
        }

        private static void SetField(object target, string name, object value)
        {
            if (target == null) throw new InvalidOperationException("Game object is unavailable: " + name);
            var field = HarmonyLib.AccessTools.Field(target.GetType(), name);
            if (field == null) throw new MissingFieldException(target.GetType().Name, name);
            field.SetValue(target, value);
        }

        private static object InvokeGame(object target, string name, params object[] arguments)
        {
            if (target == null) throw new InvalidOperationException("Game object is unavailable: " + name);
            var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == arguments.Length &&
                    m.GetParameters().Select((p, i) => arguments[i] == null || p.ParameterType.IsInstanceOfType(arguments[i])).All(x => x));
            if (method == null) throw new MissingMethodException(target.GetType().Name, name);
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static void RunHost(Action action)
        {
            if (!NetworkServer.active) { SetStatus("Нужен активный host/server"); return; }
            RunAction(action);
        }

        private static void RunAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { SetStatus(ex.GetType().Name + ": " + ex.Message); if (Log != null) Log.LogError(ex); }
        }

        private static bool Button(string label, Action action, bool host = true)
        {
            var previous = GUI.enabled;
            GUI.enabled = previous && (!host || NetworkServer.active);
            var pressed = GUILayout.Button(label);
            GUI.enabled = previous;
            if (pressed) { if (host) RunHost(action); else RunAction(action); }
            return pressed;
        }

        private string EditText(string key, string label, string initial = "")
        {
            string value;
            if (!_editBuffers.TryGetValue(key, out value)) value = initial;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(240));
            GUI.SetNextControlName("CasinoMenu." + key);
            value = GUILayout.TextField(value ?? "");
            GUILayout.EndHorizontal();
            _editBuffers[key] = value;
            return value;
        }

        private static float Slider(string label, ConfigEntry<float> entry, float min, float max)
        {
            GUILayout.Label(label + ": " + entry.Value.ToString("0.##"));
            var value = Mathf.Clamp(GUILayout.HorizontalSlider(entry.Value, min, max), min, max);
            if (!Mathf.Approximately(entry.Value, value)) entry.Value = value;
            return value;
        }

    }
}
