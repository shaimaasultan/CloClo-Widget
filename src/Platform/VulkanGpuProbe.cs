using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// Finds a dedicated GPU for local Arabic transcription through the Windows
// Vulkan loader, loaded by its full System32 path. Only device properties are
// read; no GPU work is submitted here.
internal static class VulkanGpuProbe
{
    // The large model needs about 1 GB of video memory; leave headroom for
    // other apps rather than competing for a small card.
    private const long MinimumDeviceLocalBytes = 3L * 1024 * 1024 * 1024;
    private const int PhysicalDeviceTypeDiscrete = 2;
    private const int MemoryHeapDeviceLocal = 1;

    internal sealed record Gpu(int Index, string Name);

    private delegate int CreateInstance(IntPtr createInfo, IntPtr allocator, out IntPtr instance);
    private delegate void DestroyInstance(IntPtr instance, IntPtr allocator);
    private delegate int EnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);
    private delegate void GetPhysicalDeviceProperties(IntPtr device, IntPtr properties);
    private delegate void GetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr properties);

    internal static string LoaderPath => Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");

    // Implicit layers are registered by other software (overlays, capture and
    // monitoring tools) and would otherwise be loaded into CloClo with the
    // Vulkan loader. Neither the probe nor transcription needs them.
    internal static void DisableImplicitLayers() =>
        NativeEnvironment.Set("VK_LOADER_LAYERS_DISABLE", "~implicit~");

    // Returns the first dedicated GPU with enough video memory, by its index
    // in the loader's device list (the index GGML_VK_VISIBLE_DEVICES uses).
    public static Gpu? FindDedicatedGpu()
    {
        if (!File.Exists(LoaderPath)) return null;
        DisableImplicitLayers();
        IntPtr library = IntPtr.Zero, instance = IntPtr.Zero, createInfo = IntPtr.Zero;
        IntPtr devices = IntPtr.Zero, properties = IntPtr.Zero, memory = IntPtr.Zero;
        DestroyInstance? destroy = null;
        try
        {
            library = NativeLibrary.Load(LoaderPath);
            T Export<T>(string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            var create = Export<CreateInstance>("vkCreateInstance");
            destroy = Export<DestroyInstance>("vkDestroyInstance");
            var enumerate = Export<EnumeratePhysicalDevices>("vkEnumeratePhysicalDevices");
            var getProperties = Export<GetPhysicalDeviceProperties>("vkGetPhysicalDeviceProperties");
            var getMemory = Export<GetPhysicalDeviceMemoryProperties>("vkGetPhysicalDeviceMemoryProperties");

            // VkInstanceCreateInfo with no application info, layers or extensions.
            createInfo = AllocZeroed(64);
            Marshal.WriteInt32(createInfo, 0, 1); // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
            if (create(createInfo, IntPtr.Zero, out instance) != 0) return null;

            uint count = 0;
            if (enumerate(instance, ref count, IntPtr.Zero) != 0 || count == 0) return null;
            devices = AllocZeroed((int)count * IntPtr.Size);
            if (enumerate(instance, ref count, devices) != 0) return null;

            properties = AllocZeroed(1024); // VkPhysicalDeviceProperties is 824 bytes
            memory = AllocZeroed(1024);     // VkPhysicalDeviceMemoryProperties is 520 bytes
            for (var i = 0; i < (int)count; i++)
            {
                var device = Marshal.ReadIntPtr(devices, i * IntPtr.Size);
                getProperties(device, properties);
                if (Marshal.ReadInt32(properties, 16) != PhysicalDeviceTypeDiscrete) continue;
                getMemory(device, memory);
                var heapCount = Math.Min(Marshal.ReadInt32(memory, 260), 16);
                long deviceLocal = 0;
                for (var h = 0; h < heapCount; h++)
                {
                    var heap = 264 + h * 16;
                    if ((Marshal.ReadInt32(memory, heap + 8) & MemoryHeapDeviceLocal) != 0)
                        deviceLocal = Math.Max(deviceLocal, Marshal.ReadInt64(memory, heap));
                }
                if (deviceLocal < MinimumDeviceLocalBytes) continue;
                var name = Marshal.PtrToStringUTF8(properties + 20) ?? "GPU";
                return new Gpu(i, name);
            }
            return null;
        }
        catch
        {
            // Missing exports, a failing driver, or no Vulkan support at all:
            // transcription stays on the CPU.
            return null;
        }
        finally
        {
            if (instance != IntPtr.Zero) try { destroy?.Invoke(instance, IntPtr.Zero); } catch { /* driver cleanup */ }
            foreach (var block in new[] { createInfo, devices, properties, memory })
                if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }
    }

    private static IntPtr AllocZeroed(int bytes)
    {
        var block = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(new byte[bytes], 0, block, bytes);
        return block;
    }
}

// The native speech runtime reads settings through the C runtime's getenv,
// which keeps its own copy of the environment. Set both that copy and the
// process environment (read by the Vulkan loader).
internal static class NativeEnvironment
{
    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try { _ = _wputenv_s(name, value); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ucrtbase.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int _wputenv_s(string name, string value);
}
