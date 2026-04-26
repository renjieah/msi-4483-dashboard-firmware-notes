using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PanelRuntime.Services;

internal static class NativeHid
{
    public const ushort VidMsi = 0x0DB0;
    public const ushort PidDashboard = 0x4BB6;
    public static readonly ushort[] PidNuc126List = [0xE777, 0x77A5, 0x0ABE];

    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public static string? FindDevicePath(ushort vid, ushort pid)
    {
        foreach (var path in EnumerateHidDevicePaths())
        {
            var needle = $"vid_{vid:x4}&pid_{pid:x4}";
            if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        return null;
    }

    public static SafeFileHandle OpenReadWrite(string path)
    {
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFileW failed: {path}");
        }
        return handle;
    }

    public static int GetOutputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return 64;
        }

        try
        {
            var status = HidP_GetCaps(preparsed, out var caps);
            return status == 0 && caps.OutputReportByteLength > 0
                ? caps.OutputReportByteLength
                : 64;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    public static void WriteFileExact(SafeFileHandle handle, byte[] buffer)
    {
        if (!WriteFile(handle, buffer, buffer.Length, out var written, IntPtr.Zero) || written != buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile failed: {written}/{buffer.Length}");
        }
    }

    public static int ReadFileBlocking(SafeFileHandle handle, byte[] buffer)
    {
        if (!ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed.");
        }
        return read;
    }

    public static bool TrySetOutputReport(SafeFileHandle handle, byte[] buffer)
    {
        return HidD_SetOutputReport(handle, buffer, buffer.Length);
    }

    public static bool TryGetInputReport(SafeFileHandle handle, byte[] buffer)
    {
        return HidD_GetInputReport(handle, buffer, buffer.Length);
    }

    public static bool DeviceOnline(ushort vid, ushort pid) => FindDevicePath(vid, pid) is not null;

    private static IEnumerable<string> EnumerateHidDevicePaths()
    {
        HidD_GetHidGuid(out var hidGuid);
        var infoSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (infoSet == IntPtr.Zero || infoSet == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            var index = 0u;
            while (true)
            {
                var data = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref data))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == 259) yield break;
                    yield break;
                }

                SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        var pathPtr = IntPtr.Add(detail, 4);
                        var path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            yield return path;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }

                index++;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(infoSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);
}
