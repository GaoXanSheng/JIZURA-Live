using System.Runtime.InteropServices;

namespace VkSpike;

/// <summary>极简 Vulkan 设备创建（P/Invoke vulkan-1.dll）：供 SkiaSharp GRVkBackendContext 用。
/// 结构体按 Vulkan 头文件 x64 布局（Sequential 自动对齐）。</summary>
internal static class Vk
{
    private const string Dll = "vulkan-1.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct VkApplicationInfo
    {
        public int sType;
        public IntPtr pNext;
        public IntPtr pApplicationName;
        public int applicationVersion;
        public IntPtr pEngineName;
        public int engineVersion;
        public int apiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public int flags;
        public IntPtr pApplicationInfo;
        public int enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public int enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkQueueFamilyProperties
    {
        public int queueFlags;
        public int queueCount;
        public int timestampValidBits;
        public int minImageTransferGranularity;
        public int pad1;
        public int pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkDeviceQueueCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public int flags;
        public int queueFamilyIndex;
        public int queueCount;
        public IntPtr pQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkDeviceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public int flags;
        public int queueCreateInfoCount;
        public IntPtr pQueueCreateInfos;
        public int enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public int enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
        public IntPtr pEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkPhysicalDeviceProperties
    {
        public int sType;
        public IntPtr pNext;
        public int apiVersion;
        public int driverVersion;
        public int vendorID;
        public int deviceID;
        public int deviceType;
        public fixed byte deviceName[256];
    }

    [DllImport(Dll)] private static extern int vkCreateInstance(ref VkInstanceCreateInfo ci, IntPtr alloc, out IntPtr instance);
    [DllImport(Dll)] private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr[]? devices);
    [DllImport(Dll)] private static extern void vkGetPhysicalDeviceProperties(IntPtr phys, out VkPhysicalDeviceProperties props);
    [DllImport(Dll)] private static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr phys, ref uint count, [Out] VkQueueFamilyProperties[]? props);
    [DllImport(Dll)] private static extern int vkCreateDevice(IntPtr phys, ref VkDeviceCreateInfo ci, IntPtr alloc, out IntPtr device);
    [DllImport(Dll)] private static extern void vkGetDeviceQueue(IntPtr device, uint family, uint index, out IntPtr queue);
    [DllImport(Dll)] private static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport(Dll)] private static extern IntPtr vkGetDeviceProcAddr(IntPtr device, [MarshalAs(UnmanagedType.LPStr)] string name);

    public static IntPtr Instance, Physical, Device, Queue;
    public static uint QueueFamily;
    public static string DeviceName = "";

    public static unsafe bool Init(out string log)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var appName = Marshal.StringToHGlobalAnsi("JizuraVkSpike");
            var engineName = Marshal.StringToHGlobalAnsi("JizuraLive");
            var app = new VkApplicationInfo
            {
                sType = 0, pNext = IntPtr.Zero, pApplicationName = appName,
                applicationVersion = 1, pEngineName = engineName, engineVersion = 1,
                apiVersion = 0x00402000,
            };
            var extName = Marshal.StringToHGlobalAnsi("VK_KHR_get_physical_device_properties2");
            var extArr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(extArr, extName);
            var appPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(app, appPtr, false);

            var ci = new VkInstanceCreateInfo
            {
                sType = 1, pNext = IntPtr.Zero, flags = 0, pApplicationInfo = appPtr,
                enabledLayerCount = 0, ppEnabledLayerNames = IntPtr.Zero,
                enabledExtensionCount = 1, ppEnabledExtensionNames = extArr,
            };

            var r = vkCreateInstance(ref ci, IntPtr.Zero, out Instance);
            if (r != 0)
            {
                ci.enabledExtensionCount = 0;
                ci.ppEnabledExtensionNames = IntPtr.Zero;
                r = vkCreateInstance(ref ci, IntPtr.Zero, out Instance);
                sb.AppendLine("vkCreateInstance = " + r + "（扩展回退）");
            }
            else sb.AppendLine("vkCreateInstance 成功（含 properties2 扩展）");
            if (r != 0) { log = sb + "vkCreateInstance=" + r + " 失败"; return false; }

            uint count = 0;
            vkEnumeratePhysicalDevices(Instance, ref count, null);
            if (count == 0) { log = sb + "无物理设备"; return false; }
            var devs = new IntPtr[count];
            vkEnumeratePhysicalDevices(Instance, ref count, devs);

            var best = IntPtr.Zero;
            var bestType = -1;
            for (var i = 0; i < count; i++)
            {
                vkGetPhysicalDeviceProperties(devs[i], out var props);
                var name = Marshal.PtrToStringAnsi((IntPtr)props.deviceName) ?? "?";
                sb.AppendLine("  [" + i + "] type=" + props.deviceType + " api=0x" + props.apiVersion.ToString("x") + " " + name);
                if (best == IntPtr.Zero || (props.deviceType == 2 && bestType != 2))
                {
                    best = devs[i];
                    bestType = props.deviceType;
                }
            }
            Physical = best;
            vkGetPhysicalDeviceProperties(Physical, out var bp);
            DeviceName = Marshal.PtrToStringAnsi((IntPtr)bp.deviceName) ?? "?";

            uint qfCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(Physical, ref qfCount, null);
            var qf = new VkQueueFamilyProperties[qfCount];
            vkGetPhysicalDeviceQueueFamilyProperties(Physical, ref qfCount, qf);
            QueueFamily = uint.MaxValue;
            for (var i = 0; i < qfCount; i++)
                if ((qf[i].queueFlags & 1) != 0) { QueueFamily = (uint)i; break; }
            if (QueueFamily == uint.MaxValue) { log = sb + "无图形队列族"; return false; }

            var prio = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(prio, 0x3F800000);
            var qci = new VkDeviceQueueCreateInfo
            {
                sType = 2, flags = 0, queueFamilyIndex = (int)QueueFamily,
                queueCount = 1, pQueuePriorities = prio,
            };
            var qciPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkDeviceQueueCreateInfo>());
            Marshal.StructureToPtr(qci, qciPtr, false);
            var dci = new VkDeviceCreateInfo
            {
                sType = 3, queueCreateInfoCount = 1, pQueueCreateInfos = qciPtr,
                enabledLayerCount = 0, ppEnabledLayerNames = IntPtr.Zero,
                enabledExtensionCount = 0, ppEnabledExtensionNames = IntPtr.Zero,
                pEnabledFeatures = IntPtr.Zero,
            };
            r = vkCreateDevice(Physical, ref dci, IntPtr.Zero, out Device);
            if (r != 0) { log = sb + "vkCreateDevice = " + r; return false; }
            vkGetDeviceQueue(Device, QueueFamily, 0, out Queue);
            sb.AppendLine("设备就绪：" + DeviceName + "（队列族 " + QueueFamily + "）");
            log = sb.ToString();
            return true;
        }
        catch (Exception ex)
        {
            log = sb + "异常：" + ex;
            return false;
        }
    }

    public static IntPtr InstanceProc(IntPtr instance, string name) => vkGetInstanceProcAddr(instance, name);
    public static IntPtr DeviceProc(IntPtr device, string name) => vkGetDeviceProcAddr(device, name);
}
