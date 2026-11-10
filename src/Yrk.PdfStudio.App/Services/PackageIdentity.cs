using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Yrk.PdfStudio.App.Services;

public enum PackageIdentityState { Unknown, Unpackaged, Packaged }

/// <summary>Windows package identity, independent of the build flag and the update feed.</summary>
public sealed class PackageIdentity
{
    internal const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE
    internal const int InsufficientBuffer = 122;
    internal delegate int NameQuery(ref uint length, StringBuilder? name);

    private static readonly Lazy<PackageIdentity> ProcessIdentity = new(() =>
        CreateOrUnpackaged(GetCurrentPackageFullName, GetCurrentPackageFamilyName));

    /// <summary>
    /// Runs before any window, so an unexpected Windows answer must not stop the app from starting.
    /// Keep startup working, but disable Velopack until Windows can positively confirm an unpackaged process.
    /// </summary>
    internal static PackageIdentity CreateOrUnpackaged(NameQuery fullName, NameQuery familyName)
    {
        try { return new PackageIdentity(fullName, familyName); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OverflowException
            or EntryPointNotFoundException or DllNotFoundException)
        {
            return new PackageIdentity();
        }
    }
    private readonly Lazy<string?> _familyName;

    public static PackageIdentity Current => ProcessIdentity.Value;

    public static bool IsStoreBuild
    {
        get
        {
#if YRK_STORE
            return true;
#else
            return false;
#endif
        }
    }

    public bool HasIdentity => FullName is not null;
    public PackageIdentityState State { get; }
    public string? FullName { get; }
    public bool UsesStoreUpdates => IsStoreBuild || HasIdentity;
    public bool CanUseVelopack => !IsStoreBuild && State == PackageIdentityState.Unpackaged;

    private PackageIdentity()
    {
        State = PackageIdentityState.Unknown;
        _familyName = new(() => null);
    }

    // Inject the native calls per instance; tests never change the process-wide identity.
    internal PackageIdentity(NameQuery fullName, NameQuery familyName)
    {
        FullName = ReadName(fullName);
        State = FullName is null ? PackageIdentityState.Unpackaged : PackageIdentityState.Packaged;
        _familyName = new(() => HasIdentity ? ReadName(familyName) : null);
    }

    private static string? ReadName(NameQuery query)
    {
        uint length = 0;
        var result = query(ref length, null);
        if (result == NoPackage) return null;
        if (result != InsufficientBuffer) throw new Win32Exception(result);
        var name = new StringBuilder(checked((int)length));
        result = query(ref length, name);
        if (result != 0) throw new Win32Exception(result);
        if (name.Length == 0) throw new InvalidOperationException("Windows returned an empty package name.");
        return name.ToString();
    }

    /// <summary>Explorer runs outside the package: give it physical paths, including legacy data if present.</summary>
    public IReadOnlyList<string> GetDataFolders() => GetDataFolders(
        HasIdentity ? UnredirectedLocalAppData() : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Directory.Exists);

    internal IReadOnlyList<string> GetDataFolders(string localAppData, Func<string, bool> directoryExists)
    {
        var legacy = Path.Combine(localAppData, "YRK", "EasyPdf");
        if (!HasIdentity) return [legacy];
        var family = _familyName.Value ?? throw new InvalidOperationException("Windows returned no package family name.");
        var packaged = Path.Combine(localAppData, "Packages", family, "LocalCache", "Local", "YRK", "EasyPdf");
        return directoryExists(legacy) ? [packaged, legacy] : [packaged];
    }

    private static string UnredirectedLocalAppData()
    {
        var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"); // FOLDERID_LocalAppData
        // KF_FLAG_NO_PACKAGE_REDIRECTION: .NET's special-folder lookup can return a package-private path.
        var result = SHGetKnownFolderPath(in folder, 0x00010000, IntPtr.Zero, out var path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) ?? throw new InvalidOperationException("LocalAppData is unavailable.");
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? name);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);
}
