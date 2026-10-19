using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

internal static class InspectAssembly
{
    private static void Main(string[] args)
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
        resolver.AddSearchDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends\Gamble With Your Friends_Data\Managed");
        resolver.AddSearchDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends\BepInEx\core");
        var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
        foreach (var type in assembly.MainModule.Types) Print(type, args.Length > 1 ? args[1] : "");
    }

    private static void Print(TypeDefinition type, string filter)
    {
        var parts = filter.Split(new[] { "::" }, StringSplitOptions.None);
        var typeFilter = parts[0];
        if (filter.Length == 0 || type.FullName.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Console.WriteLine("TYPE " + type.FullName + " : " + type.BaseType);
            foreach (var field in type.Fields) Console.WriteLine("  FIELD " + field.Attributes + " " + field.FieldType + " " + field.Name);
            foreach (var prop in type.Properties) Console.WriteLine("  PROPERTY " + prop.PropertyType + " " + prop.Name);
            foreach (var method in type.Methods)
            {
                if (parts.Length > 1 && method.Name.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                Console.WriteLine("  METHOD " + method.Attributes + " " + method.FullName);
                if (filter.Length != 0 && method.HasBody)
                    foreach (var instruction in method.Body.Instructions) Console.WriteLine("    " + instruction);
            }
        }
        foreach (var nested in type.NestedTypes) Print(nested, filter);
    }
}
