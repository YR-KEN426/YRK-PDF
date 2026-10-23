using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Yrk.PdfStudio.App.Services;

/// <summary>
/// Registers YRK PDF for PDF files for the current user: it appears under "Open with" and in
/// Settings › Default apps, but it never takes over the default viewer by itself (Windows leaves that
/// choice to the user). Called by the installer hooks; nothing here needs administrator rights.
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "YRK.EasyPdf.Document";
    private const string AppName = "YRK PDF";
    /// <summary>The name up to 1.0.7; its entry in RegisteredApplications is removed so Default apps lists the app once.</summary>
    private const string OldAppName = "YRK Easy PDF";
    private const string CapabilitiesKey = @"Software\YRK\EasyPdf\Capabilities";
    private const string ExeName = "YrkEasyPdf.exe";
    private const string DrawingProgId = "YRK.EasyPdf.Drawing";

    private static readonly (string ProgId, string TypeName)[] ProgIds =
    [
        (ProgId, "PDF document"),
        (DrawingProgId, "AutoCAD drawing"),
    ];

    /// <summary>Files offered under "Open with". Illustrator files open when saved PDF-compatible.</summary>
    private static readonly (string Extension, string ProgId)[] Extensions =
    [
        (".pdf", ProgId),
        (".ai", ProgId),
        (".dwg", DrawingProgId),
        (".dxf", DrawingProgId),
    ];

    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var command = $"\"{exe}\" \"%1\"";
        foreach (var (progIdName, typeName) in ProgIds)
        {
            using var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progIdName}");
            progId.SetValue(null, typeName);
            progId.SetValue("FriendlyTypeName", typeName);
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        foreach (var (extension, progIdName) in Extensions)
        {
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}\OpenWithProgids");
            openWith.SetValue(progIdName, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var application = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExeName}"))
        {
            application.SetValue("FriendlyAppName", AppName);
            using var types = application.CreateSubKey("SupportedTypes");
            foreach (var (extension, _) in Extensions)
            {
                types.SetValue(extension, string.Empty);
            }

            using var open = application.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "Read, fill, sign, comment and edit PDF documents, and view AutoCAD drawings.");
            capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var (extension, progIdName) in Extensions)
            {
                associations.SetValue(extension, progIdName);
            }
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.DeleteValue(OldAppName, throwOnMissingValue: false);
            registered.SetValue(AppName, CapabilitiesKey);
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        foreach (var (progIdName, _) in ProgIds)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progIdName}", throwOnMissingSubKey: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\YRK\EasyPdf", throwOnMissingSubKey: false);
        foreach (var (extension, progIdName) in Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(progIdName, throwOnMissingValue: false);
        }

        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
            registered?.DeleteValue(OldAppName, throwOnMissingValue: false);
        }

        NotifyShell();
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
