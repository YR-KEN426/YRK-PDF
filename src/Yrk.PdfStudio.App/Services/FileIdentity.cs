using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yrk.PdfStudio.App.Services;

internal static class FileIdentity
{
    public static bool AreSameFile(string source, string target)
    {
        try
        {
            const FileShare sharing = FileShare.ReadWrite | FileShare.Delete;
            using var sourceHandle = File.OpenHandle(source, FileMode.Open, FileAccess.Read, sharing);
            using var targetHandle = File.OpenHandle(target, FileMode.Open, FileAccess.Read, sharing);
            var informationSize = (uint)Marshal.SizeOf<FileIdInformation>();
            if (GetFileInformationByHandleEx(sourceHandle, FileInfoByHandleClass.FileIdInfo, out var sourceId, informationSize)
                && GetFileInformationByHandleEx(targetHandle, FileInfoByHandleClass.FileIdInfo, out var targetId, informationSize))
            {
                return sourceId.VolumeSerialNumber == targetId.VolumeSerialNumber
                    && sourceId.FileId.Low == targetId.FileId.Low
                    && sourceId.FileId.High == targetId.FileId.High;
            }

            // Fall back for both handles so identities always use the same format.
            if (GetFileInformationByHandle(sourceHandle, out var sourceInfo)
                && GetFileInformationByHandle(targetHandle, out var targetInfo))
            {
                return sourceInfo.VolumeSerialNumber == targetInfo.VolumeSerialNumber
                    && sourceInfo.FileIndexHigh == targetInfo.FileIndexHigh
                    && sourceInfo.FileIndexLow == targetInfo.FileIndexLow;
            }
        }
        catch (Exception)
        {
            // Fail closed: an unreadable identity may belong to the source file.
            return true;
        }

        // Fail closed when neither identity API can establish that files differ.
        return true;
    }

    private enum FileInfoByHandleClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        public ulong Low;
        public ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public FileId128 FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, FileInfoByHandleClass informationClass,
        out FileIdInformation information, uint bufferSize);

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
