using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yrk.PdfStudio.App.Services;

internal static class FileIdentity
{
    public static bool AreSameFile(string source, string target)
    {
        const FileShare sharing = FileShare.ReadWrite | FileShare.Delete;
        using var sourceHandle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, sharing);
        using var targetHandle = File.OpenHandle(target, FileMode.Open, FileAccess.Read, sharing);
        var sourceInfo = Read(sourceHandle);
        var targetInfo = Read(targetHandle);
        return sourceInfo.VolumeSerialNumber == targetInfo.VolumeSerialNumber
            && sourceInfo.FileIndexHigh == targetInfo.FileIndexHigh
            && sourceInfo.FileIndexLow == targetInfo.FileIndexLow;
    }

    private static ByHandleFileInformation Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        return information;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
