using System;
using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.SshNet.Tests.Storage;

[Collection("SshNetStorageIntegrationTests")]
public class SshNetStorageTests : FileStorageTestsBase
{
    public SshNetStorageTests(ITestOutputHelper output) : base(output) { }

    protected override IFileStorage GetStorage()
    {
        string connectionString = Configuration.GetConnectionString("SshNetStorageConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        return new ScopedFileStorage(new SshNetFileStorage(o => o.ConnectionString(connectionString).LoggerFactory(Log)), "storage");
    }

    [Fact]
    public override Task CanGetEmptyFileListOnMissingDirectoryAsync()
    {
        return base.CanGetEmptyFileListOnMissingDirectoryAsync();
    }

    [Fact]
    public override Task CanGetFileListForSingleFolderAsync()
    {
        return base.CanGetFileListForSingleFolderAsync();
    }

    [Fact]
    public override Task CanGetFileListForSingleFileAsync()
    {
        return base.CanGetFileListForSingleFileAsync();
    }

    [Fact]
    public override Task CanGetPagedFileListForSingleFolderAsync()
    {
        return base.CanGetPagedFileListForSingleFolderAsync();
    }

    [Fact]
    public override Task CanGetFileInfoAsync()
    {
        return base.CanGetFileInfoAsync();
    }

    [Fact]
    public override Task CanGetNonExistentFileInfoAsync()
    {
        return base.CanGetNonExistentFileInfoAsync();
    }

    [Fact]
    public override Task CanSaveFilesAsync()
    {
        return base.CanSaveFilesAsync();
    }

    [Fact]
    public override Task CanManageFilesAsync()
    {
        return base.CanManageFilesAsync();
    }

    [Fact]
    public override Task CanRenameFilesAsync()
    {
        return base.CanRenameFilesAsync();
    }

    [Fact]
    public override Task CanConcurrentlyManageFilesAsync()
    {
        return base.CanConcurrentlyManageFilesAsync();
    }

    [Fact]
    public override void CanUseDataDirectory()
    {
        base.CanUseDataDirectory();
    }

    [Fact]
    public override Task CanDeleteEntireFolderAsync()
    {
        return base.CanDeleteEntireFolderAsync();
    }

    [Fact]
    public override Task CanDeleteEntireFolderWithWildcardAsync()
    {
        return base.CanDeleteEntireFolderWithWildcardAsync();
    }

    [Fact]
    public override Task CanDeleteFolderWithMultiFolderWildcardsAsync()
    {
        return base.CanDeleteFolderWithMultiFolderWildcardsAsync();
    }

    [Fact]
    public override Task CanDeleteSpecificFilesAsync()
    {
        return base.CanDeleteSpecificFilesAsync();
    }

    [Fact]
    public override Task CanDeleteNestedFolderAsync()
    {
        return base.CanDeleteNestedFolderAsync();
    }

    [Fact]
    public override Task CanDeleteSpecificFilesInNestedFolderAsync()
    {
        return base.CanDeleteSpecificFilesInNestedFolderAsync();
    }

    [Fact]
    public override Task CanRoundTripSeekableStreamAsync()
    {
        return base.CanRoundTripSeekableStreamAsync();
    }

    [Fact]
    public override Task WillRespectStreamOffsetAsync()
    {
        return base.WillRespectStreamOffsetAsync();
    }

    [Fact(Skip = "Write Stream is not yet supported")]
    public override Task WillWriteStreamContentAsync()
    {
        return base.WillWriteStreamContentAsync();
    }

    [Fact]
    public override Task CanSaveOverExistingStoredContent()
    {
        return base.CanSaveOverExistingStoredContent();
    }

    [Fact]
    public void CanCreateSshNetFileStorageWithoutConnectionStringPassword()
    {
        //Arrange
        var options = new SshNetFileStorageOptionsBuilder()
            .ConnectionString("sftp://username@host")
            .Build();

        //Act
        var storage = new SshNetFileStorage(options);
        Assert.NotNull(storage);
    }

    [Fact]
    public void CanCreateSshNetFileStorageWithoutProxyPassword()
    {
        //Arrange
        var options = new SshNetFileStorageOptionsBuilder()
            .ConnectionString("sftp://username@host")
            .Proxy("proxy://username@host")
            .Build();

        //Act
        var storage = new SshNetFileStorage(options);
        Assert.NotNull(storage);
    }

    [Fact]
    public virtual async Task WillNotReturnDirectoryInGetPagedFileListAsync()
    {
        var storage = GetStorage();
        if (storage == null)
            return;

        await ResetAsync(storage);

        using (storage)
        {
            var result = await storage.GetPagedFileListAsync();
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);

            const string directory = "EmptyDirectory";
            var client = storage is ScopedFileStorage { UnscopedStorage: SshNetFileStorage sshNetStorage } ? sshNetStorage.GetClient() : null;
            Assert.NotNull(client);

            client.CreateDirectory($"storage/{directory}");

            result = await storage.GetPagedFileListAsync();
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);
            Assert.False(await result.NextPageAsync());
            Assert.False(result.HasMore);
            Assert.Empty(result.Files);

            // Ensure the directory will not be returned via get file info
            var info = await storage.GetFileInfoAsync(directory);
            Assert.Null(info?.Path);

            // Ensure delete files can remove all files including fake folders
            await storage.DeleteFilesAsync("*");

            // Assert folder was removed by Delete Files
            Assert.False(client.Exists($"storage/{directory}"));

            info = await storage.GetFileInfoAsync(directory);
            Assert.Null(info);
        }
    }
}
