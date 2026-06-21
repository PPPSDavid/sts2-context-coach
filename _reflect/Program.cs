using System.Reflection;
using System.Linq;

if (args.Length == 0)
{
    Console.WriteLine("Usage: Sts2Reflect <path-to-sts2.dll>");
    return 1;
}

var sts2Path = Path.GetFullPath(args[0]);
var baseDir = Path.GetDirectoryName(sts2Path)!;

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var simple = new AssemblyName(e!.Name!).Name!;
    var p = Path.Combine(baseDir, simple + ".dll");
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var sts2 = Assembly.LoadFrom(sts2Path);
const BindingFlags All = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

void DumpType(string typeName, bool allProps = false)
{
    var t = sts2.GetType(typeName);
    Console.WriteLine();
    Console.WriteLine($"=== {typeName} === exists: {t != null}");
    if (t == null) return;

    foreach (var p in t.GetProperties(All))
    {
        if (p.GetIndexParameters().Length != 0) continue;
        var n = p.Name;
        if (allProps ||
            n.Contains("Run", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Deck", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Relic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Hp", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Energy", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Ascension", StringComparison.OrdinalIgnoreCase) ||
            n is "Instance" or "Current" or "Shared" or "Singleton")
        {
            Console.WriteLine($"  prop {p.PropertyType.Name} {n}");
        }
    }

    foreach (var f in t.GetFields(All))
    {
        if (!allProps) continue;
        Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
    }
}

DumpType("MegaCrit.Sts2.Core.Saves.SaveManager");
DumpType("MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager");
DumpType("MegaCrit.Sts2.Core.Saves.SerializableRun", allProps: true);
DumpType("MegaCrit.Sts2.Core.Saves.ProfileSave");
{
    var run = sts2.GetType("MegaCrit.Sts2.Core.Saves.SerializableRun")!;
    var pl = run.GetProperty("Players", All);
    Console.WriteLine();
    Console.WriteLine($"Players element type: {pl!.PropertyType.GetGenericArguments()[0].FullName}");
    DumpType(pl.PropertyType.GetGenericArguments()[0].FullName!, allProps: true);
}

var sm = sts2.GetType("MegaCrit.Sts2.Core.Saves.SaveManager")!;
foreach (var m in sm!.GetMethods(All).Where(x => x.Name == "LoadRunSave"))
    Console.WriteLine($"LoadRunSave: ret={m.ReturnType.FullName}");

var rsr = sts2.GetType("MegaCrit.Sts2.Core.Saves.ReadSaveResult`1")?.MakeGenericType(sts2.GetType("MegaCrit.Sts2.Core.Saves.SerializableRun")!)!;
if (rsr != null)
{
    Console.WriteLine("ReadSaveResult<SerializableRun> props:");
    foreach (var p in rsr.GetProperties(All))
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
}

var rsm = sts2.GetType("MegaCrit.Sts2.Core.Saves.Managers.RunSaveManager")!;
Console.WriteLine("RunSaveManager fields:");
foreach (var f in rsm.GetFields(All))
    Console.WriteLine($"  {f.FieldType.Name} {f.Name}");
Console.WriteLine("RunSaveManager props:");
foreach (var p in rsm.GetProperties(All).Where(x => x.GetIndexParameters().Length == 0 && (x.Name.Contains("Run") || x.Name.Contains("Save") || x.Name.Contains("Data"))))
    Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");

return 0;
