using System.IO;

namespace Yrk.PdfStudio.App.Services.Backup;

[Flags]
public enum BackupSelection
{
    None = 0, Stamps = 1, Signatures = 2, DigitalIds = 4, TrustedCertificates = 8,
    ETax = 16, Settings = 32, All = 63
}

public delegate BackupManifest WriteBeforeImport(string path, string data, ReadOnlySpan<char> password, string version);
public delegate BackupImportResult ImportBackup(string path, string data, string temp, ReadOnlySpan<char> password, bool replace);

/// <summary>Fail closed: authenticate, snapshot current stores, verify snapshot, then import.</summary>
public sealed class BackupImportCoordinator
{
    public const string SafetyError = "Import stopped because a verified backup of your current data could not be created. Your data has not been imported.";
    private readonly BackupReader _reader;
    private readonly WriteBeforeImport _write;
    private readonly ImportBackup _import;

    public BackupImportCoordinator(BackupReader? reader = null, BackupWriter? writer = null,
        WriteBeforeImport? write = null, ImportBackup? import = null)
    {
        _reader = reader ?? new();
        var backupWriter = writer ?? new();
        _write = write ?? ((path, data, password, version) => backupWriter.Write(path, data, password, version,
            checkDestinationLinks: true));
        _import = import ?? _reader.Import;
    }

    public BackupImportResult Import(string path, string data, string temp, ReadOnlySpan<char> password, bool replace, string version)
    {
        _reader.Inspect(path, password); // Wrong password/damaged archive must not create even a safety backup.
        try
        {
            BackupFormat.CheckNoLinks(data);
            if (File.Exists(data)) throw new IOException();
            // Only an absent/empty set of supported stores may skip the safety backup.
            if (BackupWriter.Sources(data).Any())
            {
                var backup = BackupFormat.Under(data, "Backups/before-import-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N") + ".yrkbackup");
                BackupFormat.CheckNoLinks(backup);
                _write(backup, data, password, version);
                BackupFormat.CheckNoLinks(backup);
                var file = new FileInfo(backup);
                if (!file.Exists || file.Length <= BackupFormat.HeaderBytes + BackupFormat.TagBytes + 22
                    || file.Length > BackupFormat.MaxBackupFileBytes) throw new IOException();
                // Full authentication, hashes and bounds, with strict link checks on this internal file.
                _reader.Inspect(backup, password, checkLinks: true);
            }
        }
        catch (Exception ex) when (BackupFormat.IsInputError(ex)) { throw new IOException(Loc.T(SafetyError)); }
        return _import(path, data, temp, password, replace);
    }
}
