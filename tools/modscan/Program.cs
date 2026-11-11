using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static class Program
{
    private const string Managed = @"C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed";

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;

        string dll = args.Length > 0 ? args[0] : "";
        if (!File.Exists(dll))
        {
            Console.WriteLine("用法: scantool <mod.dll>");
            return 2;
        }

        Type scanner = Type.GetType("ModScanner, Assembly-CSharp");
        if (scanner == null)
        {
            Console.WriteLine("找不到 ModScanner 类型");
            return 3;
        }

        MethodInfo scan = scanner.GetMethod("Scan", BindingFlags.Public | BindingFlags.Static);
        object result = scan.Invoke(null, new object[]
        {
            new List<string> { dll },
            new List<string>(),
            Path.GetDirectoryName(dll),
            true
        });

        Type rt = result.GetType();
        Console.WriteLine("=== " + Path.GetFileName(dll) + " ===");
        Console.WriteLine("methods scanned: " + rt.GetField("MethodsScanned").GetValue(result));
        Console.WriteLine("error: " + (rt.GetField("Error").GetValue(result) ?? "-"));
        Console.WriteLine("capabilities: " + string.Join(", ", (HashSet<string>)rt.GetField("Capabilities").GetValue(result)));

        Dump("hard flags", rt.GetField("HardFlags").GetValue(result));
        Dump("findings", rt.GetField("Findings").GetValue(result));
        Dump("pending (12/10 之后算)", rt.GetField("Pending").GetValue(result));
        return 0;
    }

    private static void Dump(string title, object list)
    {
        var items = (System.Collections.IEnumerable)list;
        int n = 0;
        Console.WriteLine("");
        Console.WriteLine("=== " + title + " ===");
        foreach (object item in items)
        {
            n++;
            Console.WriteLine("  " + n.ToString().PadLeft(2) + ". " + item);
        }

        if (n == 0)
        {
            Console.WriteLine("  （无）");
        }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        string path = Path.Combine(Managed, name);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
