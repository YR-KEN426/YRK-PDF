using System.Runtime.InteropServices;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class FileIdentityTests
{
    [Fact]
    public void Live_identity_detects_a_hard_link_after_the_source_bytes_change()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-identity-tests-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.pdf");
            var alias = Path.Combine(folder.FullName, "alias.pdf");
            File.WriteAllBytes(source, [1, 2, 3]);
            var snapshot = File.ReadAllBytes(source);
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero));
            File.WriteAllBytes(source, [4, 5, 6]);
            Assert.False(snapshot.AsSpan().SequenceEqual(File.ReadAllBytes(alias)));
            using var open = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.True(FileIdentity.AreSameFile(source, alias));
            Assert.True(FileIdentity.AreSameFile(source, source));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void Equal_bytes_in_separate_files_have_different_identities()
    {
        var folder = Directory.CreateTempSubdirectory("yrk-identity-tests-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.pdf");
            var copy = Path.Combine(folder.FullName, "copy.pdf");
            File.WriteAllBytes(source, [1, 2, 3]);
            File.Copy(source, copy);
            Assert.False(FileIdentity.AreSameFile(source, copy));
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData("SOURCE.PDF")]
    [InlineData(@".\source.pdf")]
    [InlineData(@"sub\..\source.pdf")]
    public void Different_path_forms_have_the_same_identity(string relativeTarget)
    {
        var folder = Directory.CreateTempSubdirectory("yrk-identity-tests-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.pdf");
            File.WriteAllBytes(source, [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(folder.FullName, "sub"));
            Assert.True(FileIdentity.AreSameFile(source, Path.Combine(folder.FullName, relativeTarget)));
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Missing_files_fail_closed(bool sourceExists, bool targetExists)
    {
        var folder = Directory.CreateTempSubdirectory("yrk-identity-tests-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.pdf");
            var target = Path.Combine(folder.FullName, "target.pdf");
            if (sourceExists)
            {
                File.WriteAllBytes(source, [1, 2, 3]);
            }

            if (targetExists)
            {
                File.WriteAllBytes(target, [1, 2, 3]);
            }

            Assert.True(FileIdentity.AreSameFile(source, target));
        }
        finally { folder.Delete(true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exclusively_locked_files_fail_closed(bool lockSource)
    {
        var folder = Directory.CreateTempSubdirectory("yrk-identity-tests-");
        try
        {
            var source = Path.Combine(folder.FullName, "source.pdf");
            var target = Path.Combine(folder.FullName, "target.pdf");
            File.WriteAllBytes(source, [1, 2, 3]);
            File.Copy(source, target);
            var lockedPath = lockSource ? source : target;
            using var locked = File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() =>
            {
                using var handle = File.OpenHandle(lockedPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            });
            Assert.True(FileIdentity.AreSameFile(source, target));
        }
        finally { folder.Delete(true); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
