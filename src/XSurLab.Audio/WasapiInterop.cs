using System.Runtime.InteropServices;

namespace XSurLab.Audio;

internal static class Native
{
    internal static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    internal static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant value);
    // MMCSS "Pro Audio" raises the audio thread's scheduling class; failure only costs robustness, not correctness.
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] internal static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

internal static class AudioError
{
    internal const int UnsupportedFormat = unchecked((int)0x88890008);
    internal const int DeviceInvalidated = unchecked((int)0x88890004);
    internal const int DeviceInUse = unchecked((int)0x8889000A);
    internal const int ExclusiveNotAllowed = unchecked((int)0x8889000E);
    internal const int ServiceNotRunning = unchecked((int)0x88890010);
    internal const int BufferSizeNotAligned = unchecked((int)0x88890019);
    internal const int ResourcesInvalidated = unchecked((int)0x88890026);
}

/// <summary>Sample layouts tried for exclusive mode, in order of preference; shared mode always uses Float32.</summary>
internal enum EndpointEncoding { Float32, Int24In32, Int32, Int24, Int16 }

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort Tag, Channels;
    public int Rate, BytesPerSecond;
    public ushort BlockAlign, Bits, Extra, ValidBits;
    public uint Mask;
    public Guid SubFormat;
    public static WaveFormat Float(int channels, int rate, uint mask) => Create(EndpointEncoding.Float32, channels, rate, mask);
    public static WaveFormat Create(EndpointEncoding encoding, int channels, int rate, uint mask)
    {
        int container = Sample.Bytes(encoding);
        return new()
        {
            Tag = 0xfffe, Channels = (ushort)channels, Rate = rate, BytesPerSecond = rate * channels * container,
            BlockAlign = (ushort)(channels * container), Bits = (ushort)(container * 8), Extra = 22,
            ValidBits = (ushort)(encoding is EndpointEncoding.Int24In32 ? 24 : container * 8), Mask = mask,
            SubFormat = new Guid(encoding == EndpointEncoding.Float32 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71")
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey { public Guid Format; public uint Id; }
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), ClassInterface(ClassInterfaceType.None)]
internal class DeviceEnumeratorObject { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient callback);
}
// Windows calls these on its own notification thread. They must return quickly and never call back into the enumerator.
[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, ref WaveFormat format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}
