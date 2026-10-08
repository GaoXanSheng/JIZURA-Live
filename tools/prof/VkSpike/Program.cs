using System.Reflection;
using JizuraLive.Engine;
using SkiaSharp;

namespace VkSpike;

internal static class Program
{
    private static readonly List<string> Lines = new();
    private static void W(string s)
    {
        Lines.Add(s);
        try { Console.WriteLine(s); } catch { /* 无控制台 */ }
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台 */ }

        if (!Vk.Init(out var log))
        {
            W("Vulkan 初始化失败：\n" + log);
            return 1;
        }
        W(log);

        // 1) 反射打印 GRVkBackendContext 的真实 API 面（版本差异下的权威事实）
        var t = typeof(SkiaSharp.GRVkBackendContext);
        W("=== GRVkBackendContext 成员 ===");
        foreach (var p in t.GetProperties())
            W($"  {p.PropertyType.Name} {p.Name}");
        W("=== GRContext.CreateVulkan 重载 ===");
        foreach (var m in typeof(GRContext).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name.Contains("Vulkan")))
            W("  " + m);

        // 2) 组装 backend context 并尝试建 GRContext
        try
        {
            var ctx = new GRVkBackendContext();
            t.GetProperty("VkInstance")?.SetValue(ctx, Vk.Instance);
            t.GetProperty("VkPhysicalDevice")?.SetValue(ctx, Vk.Physical);
            t.GetProperty("VkDevice")?.SetValue(ctx, Vk.Device);
            t.GetProperty("VkQueue")?.SetValue(ctx, Vk.Queue);
            t.GetProperty("GraphicsQueueIndex")?.SetValue(ctx, Vk.QueueFamily);
            var gpa = t.GetProperty("GetProcedureAddress");
            if (gpa != null)
            {
                // delegate: IntPtr (string name, IntPtr instance, IntPtr device)
                var del = Delegate.CreateDelegate(gpa.PropertyType,
                    typeof(Program).GetMethod(nameof(GetProc), BindingFlags.Static | BindingFlags.NonPublic)!);
                gpa.SetValue(ctx, del);
            }

            var gr = GRContext.CreateVulkan(ctx);
            W(gr != null ? "*** GRContext.CreateVulkan 成功 ***" : "CreateVulkan 返回 null（失败）");
            if (gr != null)
            {
                W($"  GPU: {gr.GetType().Name}, MaxTexture {gr.GetMaxSurfaceSampleCount(SKColorType.Bgra8888)}x");
            }
        }
        catch (Exception ex)
        {
            W("CreateVulkan 异常：" + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException != null) W("  inner: " + ex.InnerException.Message);
        }
        try
        {
            var report = System.IO.Path.Combine(AppContext.BaseDirectory, "vk-report.txt");
            System.IO.File.WriteAllLines(report, Lines);
        }
        catch { /* 报告写失败不拦 */ }
        return 0;
    }

    private static IntPtr GetProc(string name, IntPtr instance, IntPtr device)
    {
        if (device != IntPtr.Zero)
        {
            var p = Vk.DeviceProc(device, name);
            if (p != IntPtr.Zero) return p;
        }
        return Vk.InstanceProc(instance, name);
    }
}
