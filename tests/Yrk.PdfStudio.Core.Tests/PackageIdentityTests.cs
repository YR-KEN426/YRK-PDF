using System.ComponentModel;
using System.Reflection;
using System.Text;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.Core.Tests;

public sealed class PackageIdentityTests
{
    internal const string FullName = "YRK.Test_1.2.0.0_x64__testpublisher";
    internal const string FamilyName = "YRK.Test_testpublisher";

    internal static PackageIdentity CreateIdentity(bool packaged = true) => new(
        Query(packaged ? FullName : null), Query(packaged ? FamilyName : null));

    internal static PackageIdentity.NameQuery Query(string? value) => (ref uint length, StringBuilder? buffer) =>
    {
        if (value is null) return PackageIdentity.NoPackage;
        if (buffer is null)
        {
            Assert.Equal(0u, length);
            length = (uint)value.Length + 1;
            return PackageIdentity.InsufficientBuffer;
        }
        Assert.Equal((uint)value.Length + 1, length);
        Assert.True(buffer.Capacity >= length);
        buffer.Append(value);
        return 0;
    };

    [Fact]
    public void No_package_is_the_only_unpacked_result_and_never_queries_family()
    {
        var calls = 0;
        var identity = new PackageIdentity((ref uint length, StringBuilder? buffer) =>
        {
            calls++;
            Assert.Null(buffer);
            return PackageIdentity.NoPackage;
        }, (ref uint length, StringBuilder? buffer) => throw new InvalidOperationException("Must not query family"));

        Assert.False(identity.HasIdentity);
        Assert.Null(identity.FullName);
        Assert.Equal(PackageIdentity.IsStoreBuild, identity.UsesStoreUpdates);
        Assert.Single(identity.GetDataFolders(@"C:\Test\Local", _ => false));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Packaged_process_reads_full_name_with_sized_buffer_and_disables_updates()
    {
        var identity = new PackageIdentity(Query(FullName),
            (ref uint length, StringBuilder? buffer) => throw new InvalidOperationException("Family is lazy"));
        Assert.Equal(FullName, identity.FullName);
        Assert.True(identity.HasIdentity);
        Assert.True(identity.UsesStoreUpdates); // Required even in an ordinary build.
    }

    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    public void Unexpected_native_errors_are_not_treated_as_unpacked(int error)
    {
        var exception = Assert.Throws<Win32Exception>(() => new PackageIdentity(
            (ref uint length, StringBuilder? buffer) => error, Query(null)));
        Assert.Equal(error, exception.NativeErrorCode);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    public void Startup_identity_never_crashes_on_an_unexpected_native_error(int error)
    {
        // Current runs before any window: a crash there would stop every GitHub copy from starting.
        var identity = PackageIdentity.CreateOrUnpackaged((ref uint length, StringBuilder? buffer) => error, Query(null));
        Assert.False(identity.HasIdentity);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(PackageIdentity.NoPackage)]
    [InlineData(PackageIdentity.InsufficientBuffer)]
    public void Failed_second_call_does_not_enable_the_regular_updater(int error)
    {
        var exception = Assert.Throws<Win32Exception>(() => new PackageIdentity(
            (ref uint length, StringBuilder? buffer) =>
            {
                if (buffer is not null) return error;
                length = 64;
                return PackageIdentity.InsufficientBuffer;
            }, Query(null)));
        Assert.Equal(error, exception.NativeErrorCode);
    }

    [Fact]
    public void Empty_successful_native_name_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new PackageIdentity(Query(""), Query(null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packaged_data_folder_uses_family_and_opens_legacy_only_if_present(bool legacyExists)
    {
        const string local = @"C:\Test Profile\AppData\Local";
        var legacy = Path.Combine(local, "YRK", "EasyPdf");
        var identity = CreateIdentity();
        var folders = identity.GetDataFolders(local, path =>
        {
            Assert.Equal(legacy, path);
            return legacyExists;
        });
        Assert.Equal(Path.Combine(local, "Packages", FamilyName, "LocalCache", "Local", "YRK", "EasyPdf"), folders[0]);
        Assert.Equal(legacyExists ? 2 : 1, folders.Count);
        if (legacyExists) Assert.Equal(legacy, folders[1]);
    }

    [Fact]
    public void Unpackaged_data_folder_is_unchanged_even_if_store_flag_is_set()
    {
        const string local = @"C:\Test\Local";
        Assert.Equal([Path.Combine(local, "YRK", "EasyPdf")],
            CreateIdentity(false).GetDataFolders(local, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void Store_build_omits_update_feed_metadata()
    {
        var feeds = typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key == "YrkUpdateUrl").ToArray();
        if (PackageIdentity.IsStoreBuild) Assert.Empty(feeds);
        else Assert.Single(feeds);
    }
}
