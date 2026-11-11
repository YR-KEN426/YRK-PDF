using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace Yrk.PdfStudio.App.Views;

internal static class BackupDialogSupport
{
    internal static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YRK", "EasyPdf");
    internal static string AppVersion => typeof(BackupDialogSupport).Assembly.GetName().Version?.ToString() ?? "1.2.0";
    internal static bool IsInsideData(string path) => Path.GetFullPath(path).StartsWith(
        DataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidDataException or CryptographicException or ArgumentException or InvalidOperationException or NotSupportedException;

    internal delegate T PasswordAction<T>(ReadOnlySpan<char> password);
    internal static Task<T> RunAsync<T>(SecureString password, PasswordAction<T> action) => Task.Run(() =>
    {
        var buffer = ReadPassword(password);
        try { return action(buffer); }
        finally { Array.Clear(buffer); }
    });

    internal static bool PasswordsMatch(SecureString first, SecureString second)
    {
        if (first.Length < 12 || first.Length != second.Length) return false;
        var a = ReadPassword(first);
        try
        {
            var b = ReadPassword(second);
            try { return CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(a.AsSpan()), MemoryMarshal.AsBytes(b.AsSpan())); }
            finally { Array.Clear(b); }
        }
        finally { Array.Clear(a); }
    }

    internal static char[] ReadPassword(SecureString password)
    {
        var result = new char[password.Length];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        try
        {
            for (var i = 0; i < result.Length; i++) result[i] = (char)Marshal.ReadInt16(pointer, i * 2);
            return result;
        }
        catch { Array.Clear(result); throw; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
}
