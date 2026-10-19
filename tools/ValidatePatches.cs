using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Reads metadata and IL only. Never loads or executes the game or plugin assemblies.
internal static class ValidatePatches
{
    private sealed class Target
    {
        internal TypeReference Type;
        internal string Name;
        internal string[] Arguments;
    }

    private static int _errors;
    private static int _targets;
    private static int _injections;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: ValidatePatches.exe <plugin.dll> <Assembly-CSharp.dll>");
            return 2;
        }
        try
        {
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
            resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
            resolver.AddSearchDirectory(Path.GetFullPath("analysis"));
            resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[1]), "..", "..", "BepInEx", "core")));
            var reader = new ReaderParameters { AssemblyResolver = resolver };
            using (var plugin = AssemblyDefinition.ReadAssembly(args[0], reader))
            using (var game = AssemblyDefinition.ReadAssembly(args[1], reader))
            {
                var gameTypes = Types(game.MainModule.Types).ToDictionary(t => t.FullName);
                foreach (var patch in Types(plugin.MainModule.Types).Where(t => HasAttribute(t, "HarmonyLib.HarmonyPatch")))
                    ValidatePatch(patch, gameTypes);
            }
            if (_targets == 0) Error("No patch targets discovered");
            Console.WriteLine("RESULT: {0} targets, {1} parameter injections, {2} errors. Metadata only; no runtime validation.", _targets, _injections, _errors);
            return _errors == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in Types(type.NestedTypes)) yield return nested;
        }
    }

    private static bool HasAttribute(ICustomAttributeProvider owner, string name)
    {
        return owner.CustomAttributes.Any(a => a.AttributeType.FullName == name);
    }

    private static void ValidatePatch(TypeDefinition patch, Dictionary<string, TypeDefinition> gameTypes)
    {
        var declared = new Target();
        foreach (var attribute in patch.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"))
        {
            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is TypeReference) declared.Type = (TypeReference)argument.Value;
                else if (argument.Type.FullName == "System.String") declared.Name = (string)argument.Value;
                else if (argument.Type.FullName == "System.Type[]")
                    declared.Arguments = ((CustomAttributeArgument[])argument.Value).Select(a => ((TypeReference)a.Value).FullName).ToArray();
                else Error(patch.Name + ": unsupported HarmonyPatch constructor argument " + argument.Type.FullName);
            }
            if (attribute.HasFields || attribute.HasProperties) Error(patch.Name + ": unsupported named HarmonyPatch arguments");
        }
        var targets = declared.Type != null && declared.Name != null
            ? new List<Target> { declared }
            : DynamicTargets(patch).ToList();
        if (targets.Count == 0) { Error(patch.Name + ": cannot determine targets"); return; }
        var hooks = patch.Methods.Where(m => m.Name == "Prefix" || m.Name == "Postfix" || m.Name == "Finalizer" ||
            HasAttribute(m, "HarmonyLib.HarmonyPrefix") || HasAttribute(m, "HarmonyLib.HarmonyPostfix") || HasAttribute(m, "HarmonyLib.HarmonyFinalizer")).ToList();
        if (hooks.Count == 0) Error(patch.Name + ": no supported prefix/postfix/finalizer found");
        var states = hooks.SelectMany(m => m.Parameters).Where(p => p.Name == "__state").Select(p => ValueType(p.ParameterType).FullName).Distinct().ToList();
        if (states.Count > 1) Error(patch.Name + ": __state types differ across hooks");
        foreach (var target in targets)
        {
            TypeDefinition owner;
            if (!gameTypes.TryGetValue(target.Type.FullName, out owner)) { Error(patch.Name + ": missing target type " + target.Type.FullName); continue; }
            var candidates = owner.Methods.Where(m => m.Name == target.Name && (target.Arguments == null ||
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(target.Arguments))).ToList();
            if (candidates.Count != 1) { Error(patch.Name + ": " + owner.FullName + "::" + target.Name + " has " + candidates.Count + " matching targets"); continue; }
            var original = candidates[0];
            var errorsBefore = _errors;
            foreach (var hook in hooks)
            {
                if (!hook.IsStatic) Error(hook.FullName + ": hook must be static");
                foreach (var parameter in hook.Parameters) ValidateInjection(original, hook, parameter);
            }
            _targets++;
            Console.WriteLine("{0}: {1} -> {2}::{3}", errorsBefore == _errors ? "PASS" : "FAIL", patch.Name, owner.FullName, original.Name);
        }
    }

    private static IEnumerable<Target> DynamicTargets(TypeDefinition patch)
    {
        // Iterator TargetMethods bodies reside in generated nested MoveNext methods.
        foreach (var method in Types(new[] { patch }).SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            TypeReference type = null;
            string name = null;
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code == Code.Ldtoken && instruction.Operand is TypeReference) type = (TypeReference)instruction.Operand;
                if (instruction.OpCode.Code == Code.Ldstr) name = (string)instruction.Operand;
                var call = instruction.Operand as MethodReference;
                if ((instruction.OpCode.Code == Code.Call || instruction.OpCode.Code == Code.Callvirt) && call != null &&
                    call.DeclaringType.FullName == "HarmonyLib.AccessTools" && call.Name == "Method")
                {
                    if (type != null && name != null) yield return new Target { Type = type, Name = name };
                    else Error(patch.Name + ": AccessTools.Method target is not a literal type/name pair");
                    type = null;
                    name = null;
                }
            }
        }
    }

    private static TypeReference ValueType(TypeReference type)
    {
        var byReference = type as ByReferenceType;
        return byReference == null ? type : byReference.ElementType;
    }

    private static bool Assignable(TypeReference value, TypeReference destination)
    {
        if (value.FullName == destination.FullName || destination.FullName == "System.Object") return true;
        var definition = value.Resolve();
        if (definition == null) return false;
        if (definition.Interfaces.Any(i => Assignable(i.InterfaceType, destination))) return true;
        return definition.BaseType != null && Assignable(definition.BaseType, destination);
    }

    private static void ValidateInjection(MethodDefinition original, MethodDefinition hook, ParameterDefinition parameter)
    {
        _injections++;
        var name = parameter.Name;
        var type = ValueType(parameter.ParameterType);
        TypeReference expected = null;
        var allowAssignable = !(parameter.ParameterType is ByReferenceType);
        if (name == "__state") return; // Consistency checked across the patch's hooks above.
        if (name == "__instance")
        {
            if (original.IsStatic) { Error(hook.FullName + ": __instance requested for static target"); return; }
            expected = original.DeclaringType;
        }
        else if (name == "__result")
        {
            if (original.ReturnType.FullName == "System.Void") { Error(hook.FullName + ": __result requested for void target"); return; }
            expected = ValueType(original.ReturnType);
        }
        else if (name.StartsWith("___", StringComparison.Ordinal))
        {
            var fieldName = name.Substring(3);
            FieldDefinition field = null;
            var owner = original.DeclaringType;
            while (owner != null && field == null)
            {
                field = owner.Fields.FirstOrDefault(f => f.Name == fieldName);
                owner = field == null && owner.BaseType != null ? owner.BaseType.Resolve() : null;
            }
            if (field == null) { Error(hook.FullName + ": injected field missing: " + fieldName); return; }
            if (original.IsStatic && !field.IsStatic) Error(hook.FullName + ": instance field requested for static target");
            expected = ValueType(field.FieldType);
        }
        else
        {
            ParameterDefinition argument;
            int index;
            if (name.StartsWith("__", StringComparison.Ordinal) && int.TryParse(name.Substring(2), out index))
                argument = index >= 0 && index < original.Parameters.Count ? original.Parameters[index] : null;
            else argument = original.Parameters.FirstOrDefault(p => p.Name == name);
            if (argument == null) { Error(hook.FullName + ": injected parameter missing: " + name); return; }
            expected = ValueType(argument.ParameterType);
        }
        if (type.FullName != expected.FullName && (!allowAssignable || !Assignable(expected, type)))
            Error(hook.FullName + ": " + name + " expects " + type.FullName + ", target provides " + expected.FullName);
    }

    private static void Error(string message)
    {
        _errors++;
        Console.Error.WriteLine("ERROR: " + message);
    }
}
