using System.IO;
using System.Reflection;

var outp = Path.Combine(AppContext.BaseDirectory, "types.txt");
var lines = new List<string>();
void Dump(Type t, string filter)
{
    lines.Add("=== " + t.FullName + " : methods containing '" + filter + "' ===");
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                 .Where(m => m.Name.Contains(filter)))
        lines.Add("  " + m);
}
var skia = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "SkiaSharp.dll"));
Dump(skia.GetType("SkiaSharp.SKSurface")!, "Create");
Dump(skia.GetType("SkiaSharp.GRContext")!, "Create");
Dump(skia.GetType("SkiaSharp.GRGlInterface")!, "Create");
File.WriteAllLines(outp, lines);
Console.WriteLine("dumped");
