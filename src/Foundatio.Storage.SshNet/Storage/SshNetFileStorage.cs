using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Foundatio.Storage;

public class SshNetFileStorage : IFileStorage
{
    private readonly ISftpClient _client;
    private readonly ISerializer _serializer;
    protected readonly ILogger _logger;

    public SshNetFileStorage(SshNetFileStorageOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;

        var connectionInfo = CreateConnectionInfo(options);
        _client = new SftpClient(connectionInfo);
    }

    public SshNetFileStorage(Builder<SshNetFileStorageOptionsBuilder, SshNetFileStorageOptions> config)
        : this(config(new SshNetFileStorageOptionsBuilder()).Build()) { }

    ISerializer IHaveSerializer.Serializer => _serializer;
    public ISftpClient GetClient()
    {
        EnsureClientConnected();
        return _client;
    }

    [Obsolete($"Use {nameof(GetFileStreamAsync)} with {nameof(FileAccess)} instead to define read or write behaviour of stream")]
    public Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default)
        => GetFileStreamAsync(path, StreamMode.Read, cancellationToken);

    public async Task<Stream> GetFileStreamAsync(string path, StreamMode streamMode, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        if (streamMode is StreamMode.Write)
            throw new NotSupportedException($"Stream mode {streamMode} is not supported.");

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);

        try
        {
            return await _client.OpenAsync(normalizedPath, FileMode.Open, FileAccess.Read, cancellationToken).AnyContext();
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogError(ex, "Unable to get file stream for {Path}: File Not Found", normalizedPath);
            return null;
        }
    }

    public Task<FileSpec> GetFileInfoAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file info for {Path}", normalizedPath);

        try
        {
            var file = _client.Get(normalizedPath);
            if (file.IsDirectory)
                return Task.FromResult<FileSpec>(null);

            return Task.FromResult(new FileSpec
            {
                Path = normalizedPath,
                Created = file.LastWriteTimeUtc,
                Modified = file.LastWriteTimeUtc,
                Size = file.Length
            });
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogError(ex, "Unable to get file info for {Path}: File Not Found", normalizedPath);
            return Task.FromResult<FileSpec>(null);
        }
    }

    public Task<bool> ExistsAsync(string path)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);
        return Task.FromResult(_client.Exists(normalizedPath));
    }

    public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Saving {Path}", normalizedPath);

        EnsureClientConnected();

        try
        {
            await using var sftpFileStream = await _client.OpenAsync(normalizedPath, FileMode.Create, FileAccess.Write, cancellationToken).AnyContext();
            await stream.CopyToAsync(sftpFileStream, cancellationToken).AnyContext();
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogDebug(ex, "Error saving {Path}: Attempting to create directory", normalizedPath);
            CreateDirectory(normalizedPath);

            _logger.LogTrace("Saving {Path}", normalizedPath);
            await using var sftpFileStream = await _client.OpenAsync(normalizedPath, FileMode.Create, FileAccess.Write, cancellationToken).AnyContext();
            await stream.CopyToAsync(sftpFileStream, cancellationToken).AnyContext();
        }

        return true;
    }

    public async Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(newPath))
            throw new ArgumentNullException(nameof(newPath));

        string normalizedPath = NormalizePath(path);
        string normalizedNewPath = NormalizePath(newPath);
        _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
        EnsureClientConnected();

        if (await ExistsAsync(normalizedNewPath).AnyContext())
        {
            _logger.LogDebug("Removing existing {NewPath} path for rename operation", normalizedNewPath);
            await DeleteFileAsync(normalizedNewPath, cancellationToken).AnyContext();
            _logger.LogDebug("Removed existing {NewPath} path for rename operation", normalizedNewPath);
        }

        try
        {
            await _client.RenameFileAsync(normalizedPath, normalizedNewPath, cancellationToken).AnyContext();
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogDebug(ex, "Error renaming {Path} to {NewPath}: Attempting to create directory", normalizedPath, normalizedNewPath);
            CreateDirectory(normalizedNewPath);

            _logger.LogTrace("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
            await _client.RenameFileAsync(normalizedPath, normalizedNewPath, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renaming {Path} to {NewPath}: {Message}", normalizedPath, normalizedNewPath, ex.Message);
            return false;
        }

        return true;
    }

    public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(targetPath))
            throw new ArgumentNullException(nameof(targetPath));

        string normalizedPath = NormalizePath(path);
        string normalizedTargetPath = NormalizePath(targetPath);
        _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);

        try
        {
            await using var stream = await GetFileStreamAsync(normalizedPath, StreamMode.Read, cancellationToken).AnyContext();
            if (stream == null)
                return false;

            return await SaveFileAsync(normalizedTargetPath, stream, cancellationToken).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying {Path} to {TargetPath}: {Message}", normalizedPath, normalizedTargetPath, ex.Message);
            return false;
        }
    }

    public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Deleting {Path}", normalizedPath);

        try
        {
            await _client.DeleteFileAsync(normalizedPath, cancellationToken).AnyContext();
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogError(ex, "Unable to delete {Path}: File not found", normalizedPath);
            return false;
        }

        return true;
    }

    public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default)
    {
        EnsureClientConnected();

        if (searchPattern == null)
            return await DeleteDirectoryAsync(_client.WorkingDirectory, false, cancellationToken);

        if (searchPattern.EndsWith("/*"))
            return await DeleteDirectoryAsync(searchPattern[..^2], false, cancellationToken);

        var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
        int count = 0;

        // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
        _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern}", files.Count, searchPattern);
        foreach (var file in files)
        {
            await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
            count++;
        }
        _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);

        return count;
    }

    private void CreateDirectory(string path)
    {
        string directory = NormalizePath(Path.GetDirectoryName(path));
        _logger.LogTrace("Ensuring {Directory} directory exists", directory);

        string[] folderSegments = directory?.Split(['/'], StringSplitOptions.RemoveEmptyEntries) ?? [];
        string currentDirectory = String.Empty;

        foreach (string segment in folderSegments)
        {
            // If current directory is empty, use current working directory instead of a rooted path.
            currentDirectory = String.IsNullOrEmpty(currentDirectory)
                ? segment
                : String.Concat(currentDirectory, "/", segment);

            if (_client.Exists(currentDirectory))
                continue;

            _logger.LogInformation("Creating {Directory} directory", currentDirectory);

            try
            {
                _client.CreateDirectory(currentDirectory);
            }
            catch (Exception ex) when (_client.Exists(currentDirectory))
            {
                _logger.LogTrace(ex, "Error creating {Directory} directory: Already exists", directory);
            }
        }
    }

    private async Task<int> DeleteDirectoryAsync(string path, bool includeSelf, CancellationToken cancellationToken = default)
    {
        int count = 0;

        string directory = NormalizePath(path);
        _logger.LogInformation("Deleting {Directory} directory", directory);

        await foreach (var file in _client.ListDirectoryAsync(directory, cancellationToken).AnyContext())
        {
            if (file.Name is "." or "..")
                continue;

            if (file.IsDirectory)
            {
                count += await DeleteDirectoryAsync(file.FullName, true, cancellationToken);
            }
            else
            {
                _logger.LogTrace("Deleting file {Path}", file.FullName);
                await _client.DeleteFileAsync(file.FullName, cancellationToken).AnyContext();
                count++;
            }
        }

        if (includeSelf)
            _client.DeleteDirectory(directory);

        _logger.LogTrace("Finished deleting {Directory} directory with {FileCount} files", directory, count);
        return count;
    }

    public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0)
            return PagedFileListResult.Empty;

        var result = new PagedFileListResult(_ => GetFiles(searchPattern, 1, pageSize, cancellationToken));
        await result.NextPageAsync().AnyContext();
        return result;
    }

    private async Task<NextPageResult> GetFiles(string searchPattern, int page, int pageSize, CancellationToken cancellationToken)
    {
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        var list = await GetFileListAsync(searchPattern, pagingLimit, skip, cancellationToken).AnyContext();
        bool hasMore = false;
        if (list.Count == pagingLimit)
        {
            hasMore = true;
            list.RemoveAt(pagingLimit - 1);
        }

        return new NextPageResult
        {
            Success = true,
            HasMore = hasMore,
            Files = list,
            NextPageFunc = hasMore ? _ => GetFiles(searchPattern, page + 1, pageSize, cancellationToken) : null
        };
    }

    private async Task<List<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0)
            return new List<FileSpec>();

        var list = new List<FileSpec>();
        var criteria = GetRequestCriteria(searchPattern);

        EnsureClientConnected();

        // NOTE: This could be very expensive the larger the directory structure you have as we aren't efficiently doing paging.
        int? recordsToReturn = limit.HasValue ? skip.GetValueOrDefault() * limit + limit : null;

        _logger.LogTrace(
            s => s.Property("SearchPattern", searchPattern).Property("Limit", limit).Property("Skip", skip),
            "Getting file list recursively matching {Prefix} and {Pattern}...", criteria.Prefix, criteria.Pattern
        );

        await GetFileListRecursivelyAsync(criteria.Prefix, criteria.Pattern, list, recordsToReturn, cancellationToken).AnyContext();

        if (skip.HasValue)
            list = list.Skip(skip.Value).ToList();

        if (limit.HasValue)
            list = list.Take(limit.Value).ToList();

        return list;
    }

    private async Task GetFileListRecursivelyAsync(string prefix, Regex pattern, ICollection<FileSpec> list, int? recordsToReturn = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Cancellation requested");
            return;
        }

        var files = new List<ISftpFile>();
        try
        {
            await foreach (var file in _client.ListDirectoryAsync(prefix, cancellationToken).AnyContext())
            {
                files.Add(file);
            }
        }
        catch (SftpPathNotFoundException)
        {
            _logger.LogDebug("Directory not found with {Prefix}", prefix);
            return;
        }

        foreach (var file in files.Where(f => f.IsRegularFile || f.IsDirectory).OrderByDescending(f => f.IsRegularFile).ThenBy(f => f.Name))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Cancellation requested");
                return;
            }

            if (recordsToReturn.HasValue && list.Count >= recordsToReturn)
                break;

            // If prefix (current directory) is empty, use current working directory instead of a rooted path.
            string path = String.IsNullOrEmpty(prefix)
                ? file.Name
                : String.Concat(prefix, "/", file.Name);

            if (file.IsDirectory)
            {
                if (file.Name is "." or "..")
                    continue;

                await GetFileListRecursivelyAsync(path, pattern, list, recordsToReturn, cancellationToken).AnyContext();
                continue;
            }

            if (!file.IsRegularFile)
                continue;

            if (pattern != null && !pattern.IsMatch(path))
            {
                _logger.LogTrace("Skipping {Path}: Doesn't match pattern", path);
                continue;
            }

            list.Add(new FileSpec
            {
                Path = path,
                Created = file.LastWriteTimeUtc,
                Modified = file.LastWriteTimeUtc,
                Size = file.Length
            });
        }
    }

    private ConnectionInfo CreateConnectionInfo(SshNetFileStorageOptions options)
    {
        if (String.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentNullException(nameof(options.ConnectionString));

        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var uri) || String.IsNullOrEmpty(uri?.UserInfo))
            throw new ArgumentException("Unable to parse connection string uri", nameof(options.ConnectionString));

        string[] userParts = uri.UserInfo.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
        string username = Uri.UnescapeDataString(userParts.First());
        string password = Uri.UnescapeDataString(userParts.Length > 1 ? userParts[1] : String.Empty);
        int port = uri.Port > 0 ? uri.Port : 22;

        var authenticationMethods = new List<AuthenticationMethod>();
        if (!String.IsNullOrEmpty(password))
            authenticationMethods.Add(new PasswordAuthenticationMethod(username, password));

        if (options.PrivateKey != null)
            authenticationMethods.Add(new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(options.PrivateKey, options.PrivateKeyPassPhrase)));

        if (authenticationMethods.Count == 0)
            authenticationMethods.Add(new NoneAuthenticationMethod(username));

        if (!String.IsNullOrEmpty(options.Proxy))
        {
            if (!Uri.TryCreate(options.Proxy, UriKind.Absolute, out var proxyUri) || String.IsNullOrEmpty(proxyUri?.UserInfo))
                throw new ArgumentException("Unable to parse proxy uri", nameof(options.Proxy));

            string[] proxyParts = proxyUri.UserInfo.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            string proxyUsername = proxyParts.First();
            string proxyPassword = proxyParts.Length > 1 ? proxyParts[1] : null;

            var proxyType = options.ProxyType;
            if (proxyType == ProxyTypes.None && proxyUri.Scheme != null && proxyUri.Scheme.StartsWith("http"))
                proxyType = ProxyTypes.Http;

            return new ConnectionInfo(uri.Host, port, username, proxyType, proxyUri.Host, proxyUri.Port, proxyUsername, proxyPassword, authenticationMethods.ToArray());
        }

        return new ConnectionInfo(uri.Host, port, username, authenticationMethods.ToArray());
    }

    private void EnsureClientConnected()
    {
        if (_client.IsConnected)
            return;

        _logger.LogTrace("Connecting to {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
        _client.Connect();
        _logger.LogTrace("Connected to {Host}:{Port} in {WorkingDirectory}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port, _client.WorkingDirectory);
    }

    private string NormalizePath(string path)
    {
        return path?.Replace('\\', '/');
    }

    private class SearchCriteria
    {
        public string Prefix { get; set; }
        public Regex Pattern { get; set; }
    }

    private SearchCriteria GetRequestCriteria(string searchPattern)
    {
        if (String.IsNullOrEmpty(searchPattern))
            return new SearchCriteria { Prefix = String.Empty };

        string normalizedSearchPattern = NormalizePath(searchPattern);
        int wildcardPos = normalizedSearchPattern.IndexOf('*');
        bool hasWildcard = wildcardPos >= 0;

        string prefix;
        Regex patternRegex;

        // NOTE: Prefix has to be a directory path, so if we do have a wildcard it needs to be part of the pattern.
        if (hasWildcard)
        {
            patternRegex = new Regex($"^{Regex.Escape(normalizedSearchPattern).Replace("\\*", ".*?")}$");
            string beforeWildcard = normalizedSearchPattern[..wildcardPos];
            int slashPos = beforeWildcard.LastIndexOf('/');
            prefix = slashPos >= 0 ? normalizedSearchPattern[..slashPos] : String.Empty;
        }
        else
        {
            patternRegex = new Regex($"^{Regex.Escape(normalizedSearchPattern)}.*?$");
            int slashPos = normalizedSearchPattern.LastIndexOf('/');
            prefix = slashPos >= 0 ? normalizedSearchPattern[..slashPos] : String.Empty;
        }

        return new SearchCriteria
        {
            Prefix = prefix,
            Pattern = patternRegex
        };
    }

    public void Dispose()
    {
        if (_client.IsConnected)
        {
            _logger.LogTrace("Disconnecting from {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
            _client.Disconnect();
            _logger.LogTrace("Disconnected from {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
        }

        _client.Dispose();
    }
}
