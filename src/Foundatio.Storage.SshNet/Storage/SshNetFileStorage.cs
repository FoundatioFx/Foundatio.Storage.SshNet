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

public class SshNetFileStorage : IFileStorage {
    private readonly ConnectionInfo _connectionInfo;
    private readonly SftpClient _client;
    private readonly ISerializer _serializer;
    protected readonly ILogger _logger;

    public SshNetFileStorage(SshNetFileStorageOptions options) {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        
        _connectionInfo = CreateConnectionInfo(options);
        _client = new SftpClient(_connectionInfo);
    }

    public SshNetFileStorage(Builder<SshNetFileStorageOptionsBuilder, SshNetFileStorageOptions> config)
        : this(config(new SshNetFileStorageOptionsBuilder()).Build()) { }

    ISerializer IHaveSerializer.Serializer => _serializer;
    public SftpClient Client => _client;

    public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file stream for {Path}", normalizedPath);
        
        try {
            var stream = new MemoryStream();
            await Task.Factory.FromAsync(_client.BeginDownloadFile(normalizedPath, stream, null, null), _client.EndDownloadFile).AnyContext();
            stream.Seek(0, SeekOrigin.Begin);

            return stream;
        } catch (SftpPathNotFoundException ex) {
            _logger.LogError(ex, "Unable to get file stream for {Path}: File Not Found", normalizedPath);
            return null;
        }
    }

    public Task<FileSpec> GetFileInfoAsync(string path) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Getting file info for {Path}", normalizedPath);
        
        try {
            var file = _client.Get(normalizedPath);
            return Task.FromResult(new FileSpec {
                Path = normalizedPath,
                Created = file.LastWriteTimeUtc,
                Modified = file.LastWriteTimeUtc,
                Size = file.Length
            });
        } catch (SftpPathNotFoundException ex) {
            _logger.LogError(ex, "Unable to get file info for {Path}: File Not Found", normalizedPath);
            return Task.FromResult<FileSpec>(null);
        }
    }

    public Task<bool> ExistsAsync(string path) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();
        
        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Checking if {Path} exists", normalizedPath);
        return Task.FromResult(_client.Exists(normalizedPath));
    }

    public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        
        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Saving {Path}", normalizedPath);
        
        EnsureClientConnected();

        try {
            await Task.Factory.FromAsync(_client.BeginUploadFile(stream, normalizedPath, null, null), _client.EndUploadFile).AnyContext();
        } catch (SftpPathNotFoundException ex) {
            _logger.LogDebug(ex, "Error saving {Path}: Attempting to create directory", normalizedPath);
            CreateDirectory(normalizedPath);
            
            _logger.LogTrace("Saving {Path}", normalizedPath);
            await Task.Factory.FromAsync(_client.BeginUploadFile(stream, normalizedPath, null, null), _client.EndUploadFile).AnyContext();
        }

        return true;
    }

    public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(newPath))
            throw new ArgumentNullException(nameof(newPath));

        string normalizedPath = NormalizePath(path);
        string normalizedNewPath = NormalizePath(newPath);
        _logger.LogInformation("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
        EnsureClientConnected();

        try {
            _client.RenameFile(normalizedPath, normalizedNewPath, true);
        } catch (SftpPathNotFoundException ex) {
            _logger.LogDebug(ex, "Error renaming {Path} to {NewPath}: Attempting to create directory", normalizedPath, normalizedNewPath);
            CreateDirectory(normalizedNewPath);
            
            _logger.LogTrace("Renaming {Path} to {NewPath}", normalizedPath, normalizedNewPath);
            _client.RenameFile(normalizedPath, normalizedNewPath, true);
        }

        return Task.FromResult(true);
    }

    public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));
        if (String.IsNullOrEmpty(targetPath))
            throw new ArgumentNullException(nameof(targetPath));

        string normalizedPath = NormalizePath(path);
        string normalizedTargetPath = NormalizePath(targetPath);
        _logger.LogInformation("Copying {Path} to {TargetPath}", normalizedPath, normalizedTargetPath);
        
        try {
            using var stream = await GetFileStreamAsync(normalizedPath, cancellationToken).AnyContext();
            if (stream == null)
                return false;

            return await SaveFileAsync(normalizedTargetPath, stream, cancellationToken).AnyContext();
        } catch (Exception ex) {
            _logger.LogError(ex, "Error copying {Path} to {TargetPath}: {Message}", normalizedPath, normalizedTargetPath, ex.Message);
            return false;
        }
    }

    public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
        if (String.IsNullOrEmpty(path))
            throw new ArgumentNullException(nameof(path));

        EnsureClientConnected();

        string normalizedPath = NormalizePath(path);
        _logger.LogTrace("Deleting {Path}", normalizedPath);
        
        try {
            _client.DeleteFile(normalizedPath);
        } catch (SftpPathNotFoundException ex) {
            _logger.LogError(ex, "Unable to delete {Path}: File not found", normalizedPath);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default) {
        EnsureClientConnected();

        if (searchPattern == null)
            return await DeleteDirectory("/", false);

        if (searchPattern.EndsWith("/*"))
            return await DeleteDirectory(searchPattern.Substring(0, searchPattern.Length - 2), false);

        var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
        int count = 0;

        // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
        _logger.LogInformation("Deleting {FileCount} files matching {SearchPattern}", files.Count, searchPattern);
        foreach (var file in files) {
            await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
            count++;
        }
        _logger.LogTrace("Finished deleting {FileCount} files matching {SearchPattern}", count, searchPattern);

        return count;
    }

    private void CreateDirectory(string path) {
        string directory = Path.GetDirectoryName(path);
        _logger.LogTrace("Ensuring {Directory} directory exists", directory);
        
        string[] folderSegments = directory.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        string currentDirectory = String.Empty;

        foreach (string segment in folderSegments) {
            currentDirectory = String.Concat(currentDirectory, "/", segment);
            if (_client.Exists(currentDirectory)) 
                continue;
            
            _logger.LogInformation("Creating {Directory} directory", directory);
            _client.CreateDirectory(currentDirectory);
        }
    }

    private async Task<int> DeleteDirectory(string path, bool includeSelf) {
        int count = 0;
        
        string directory = NormalizePath(path);
        _logger.LogInformation("Deleting {Directory} directory", directory);

        foreach (var file in await _client.ListDirectoryAsync(directory)) {
            if (file.Name is "." or "..") 
                continue;
            
            if (file.IsDirectory) {
                count += await DeleteDirectory(file.FullName, true);
            } else {
                _logger.LogTrace("Deleting file {Path}", file.FullName);
                _client.DeleteFile(file.FullName);
                count++;
            }
        }

        if (includeSelf)
            _client.DeleteDirectory(directory);

        _logger.LogTrace("Finished deleting {Directory} directory with {FileCount} files", directory, count);
        return count;
    }

    public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
        if (pageSize <= 0)
            return PagedFileListResult.Empty;

        var result = new PagedFileListResult(_ => GetFiles(searchPattern, 1, pageSize, cancellationToken));
        await result.NextPageAsync().AnyContext();
        return result;
    }

    private async Task<NextPageResult> GetFiles(string searchPattern, int page, int pageSize, CancellationToken cancellationToken) {
        int pagingLimit = pageSize;
        int skip = (page - 1) * pagingLimit;
        if (pagingLimit < Int32.MaxValue)
            pagingLimit++;

        var list = await GetFileListAsync(searchPattern, pagingLimit, skip, cancellationToken).AnyContext();
        bool hasMore = false;
        if (list.Count == pagingLimit) {
            hasMore = true;
            list.RemoveAt(pagingLimit - 1);
        }

        return new NextPageResult {
            Success = true,
            HasMore = hasMore,
            Files = list,
            NextPageFunc = hasMore ? _ => GetFiles(searchPattern, page + 1, pageSize, cancellationToken) : null
        };
    }

    private async Task<List<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default) {
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

    private async Task GetFileListRecursivelyAsync(string prefix, Regex pattern, ICollection<FileSpec> list, int? recordsToReturn = null, CancellationToken cancellationToken = default) {
        if (cancellationToken.IsCancellationRequested) {
            _logger.LogDebug("Cancellation requested");
            return;
        }

        var files = new List<SftpFile>();
        try {
            files.AddRange(await _client.ListDirectoryAsync(prefix).AnyContext());
        } catch (SftpPathNotFoundException) {
            _logger.LogDebug("Directory not found with {Prefix}", prefix);
            return;
        }

        foreach (var file in files.Where(f => f.IsRegularFile || f.IsDirectory).OrderByDescending(f => f.IsRegularFile).ThenBy(f => f.Name)) {
            if (cancellationToken.IsCancellationRequested) {
                _logger.LogDebug("Cancellation requested");
                return;
            }

            if (recordsToReturn.HasValue && list.Count >= recordsToReturn)
                break;

            if (file.IsDirectory) {
                if (file.Name is "." or "..")
                    continue;

                await GetFileListRecursivelyAsync(String.Concat(prefix, "/", file.Name), pattern, list, recordsToReturn, cancellationToken).AnyContext();
                continue;
            }

            if (!file.IsRegularFile)
                continue;

            string path = String.Concat(prefix, "/", file.Name);
            if (pattern != null && !pattern.IsMatch(path)) {
                _logger.LogTrace("Skipping {Path}: Doesn't match pattern", path);
                continue;
            }

            list.Add(new FileSpec {
                Path = path,
                Created = file.LastWriteTimeUtc,
                Modified = file.LastWriteTimeUtc,
                Size = file.Length
            });
        }
    }

    private ConnectionInfo CreateConnectionInfo(SshNetFileStorageOptions options) {
        if (String.IsNullOrEmpty(options.ConnectionString))
            throw new ArgumentNullException(nameof(options.ConnectionString));

        if (!Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var uri) || String.IsNullOrEmpty(uri?.UserInfo))
            throw new ArgumentException("Unable to parse connection string uri", nameof(options.ConnectionString));

        string[] userParts = uri.UserInfo.Split(new [] { ':' }, StringSplitOptions.RemoveEmptyEntries);
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

        if (!String.IsNullOrEmpty(options.Proxy)) {
            if (!Uri.TryCreate(options.Proxy, UriKind.Absolute, out var proxyUri) || String.IsNullOrEmpty(proxyUri?.UserInfo))
                throw new ArgumentException("Unable to parse proxy uri", nameof(options.Proxy));

            string[] proxyParts = proxyUri.UserInfo.Split(new [] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            string proxyUsername = proxyParts.First();
            string proxyPassword = proxyParts.Length > 1 ? proxyParts[1] : null;

            var proxyType = options.ProxyType;
            if (proxyType == ProxyTypes.None && proxyUri.Scheme != null && proxyUri.Scheme.StartsWith("http"))
                proxyType = ProxyTypes.Http;

            return new ConnectionInfo(uri.Host, port, username, proxyType, proxyUri.Host, proxyUri.Port, proxyUsername, proxyPassword, authenticationMethods.ToArray());
        }

        return new ConnectionInfo(uri.Host, port, username, authenticationMethods.ToArray());
    }

    private void EnsureClientConnected() {
        if (_client.IsConnected) 
            return;
        
        _logger.LogTrace("Connecting to {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
        _client.Connect();
        _logger.LogTrace("Connected to {Host}:{Port} in {WorkingDirectory}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port, _client.WorkingDirectory);
    }

    private string NormalizePath(string path) {
        return path?.Replace('\\', '/');
    }

    private class SearchCriteria {
        public string Prefix { get; set; }
        public Regex Pattern { get; set; }
    }

    private SearchCriteria GetRequestCriteria(string searchPattern) {
        if (String.IsNullOrEmpty(searchPattern))
            return new SearchCriteria { Prefix = String.Empty };

        string normalizedSearchPattern = NormalizePath(searchPattern);
        int wildcardPos = normalizedSearchPattern.IndexOf('*');
        bool hasWildcard = wildcardPos >= 0;

        string prefix;
        Regex patternRegex;

        if (hasWildcard) {
            patternRegex = new Regex($"^{Regex.Escape(normalizedSearchPattern).Replace("\\*", ".*?")}$");
            string beforeWildcard = normalizedSearchPattern.Substring(0, wildcardPos);
            int slashPos = beforeWildcard.LastIndexOf('/');
            prefix = slashPos >= 0 ? normalizedSearchPattern.Substring(0, slashPos) : String.Empty;
        } else {
            patternRegex = new Regex($"^{normalizedSearchPattern}$");
            int slashPos = normalizedSearchPattern.LastIndexOf('/');
            prefix = slashPos >= 0 ? normalizedSearchPattern.Substring(0, slashPos) : String.Empty;
        }

        return new SearchCriteria {
            Prefix = prefix,
            Pattern = patternRegex
        };
    }

    public void Dispose() {
        if (_client.IsConnected) {
            _logger.LogTrace("Disconnecting from {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
            _client.Disconnect();
            _logger.LogTrace("Disconnected from {Host}:{Port}", _client.ConnectionInfo.Host, _client.ConnectionInfo.Port);
        }

        _client.Dispose();
    }
}