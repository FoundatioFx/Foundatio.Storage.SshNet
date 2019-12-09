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

namespace Foundatio.Storage {
    public class SshNetFileStorage : IFileStorage {
        private readonly ConnectionInfo _connectionInfo;
        private readonly SftpClient _client;
        private readonly ISerializer _serializer;
        protected readonly ILogger _logger;

        public SshNetFileStorage(SshNetFileStorageOptions options) {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _connectionInfo = CreateConnectionInfo(options);
            _client = new SftpClient(_connectionInfo);

            _serializer = options.Serializer ?? DefaultSerializer.Instance;
            _logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger<SshNetFileStorage>.Instance;
        }

        public SshNetFileStorage(Builder<SshNetFileStorageOptionsBuilder, SshNetFileStorageOptions> config)
            : this(config(new SshNetFileStorageOptionsBuilder()).Build()) { }

        ISerializer IHaveSerializer.Serializer => _serializer;

        public async Task<Stream> GetFileStreamAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            EnsureClientConnected();

            try {
                var stream = new MemoryStream();
                await Task.Factory.FromAsync(_client.BeginDownloadFile(NormalizePath(path), stream, null, null), _client.EndDownloadFile).AnyContext();
                stream.Seek(0, SeekOrigin.Begin);

                return stream;
            } catch (SftpPathNotFoundException ex) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "Error trying to get file stream: {Path}", path);

                return null;
            }
        }

        public Task<FileSpec> GetFileInfoAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            EnsureClientConnected();

            try {
                var file = _client.Get(NormalizePath(path));
                    return Task.FromResult(new FileSpec {
                        Path = file.FullName.TrimStart('/'),
                        Created = file.LastWriteTimeUtc,
                        Modified = file.LastWriteTimeUtc,
                        Size = file.Length
                    });
            } catch (SftpPathNotFoundException ex) {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace(ex, "Error trying to getting file info: {Path}", path);

                return Task.FromResult<FileSpec>(null);
            }
        }

        public Task<bool> ExistsAsync(string path) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            EnsureClientConnected();
            return Task.FromResult(_client.Exists(NormalizePath(path)));
        }

        public async Task<bool> SaveFileAsync(string path, Stream stream, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            path = NormalizePath(path);
            EnsureClientConnected();
            EnsureDirectoryExists(path);

            await Task.Factory.FromAsync(_client.BeginUploadFile(stream, path, null, null), _client.EndUploadFile).AnyContext();

            return true;
        }

        public Task<bool> RenameFileAsync(string path, string newPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(newPath))
                throw new ArgumentNullException(nameof(newPath));

            newPath = NormalizePath(newPath);
            EnsureClientConnected();
            EnsureDirectoryExists(newPath);
            _client.RenameFile(NormalizePath(path), newPath, true);

            return Task.FromResult(true);
        }

        public async Task<bool> CopyFileAsync(string path, string targetPath, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (String.IsNullOrEmpty(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            using (var stream = await GetFileStreamAsync(path, cancellationToken).AnyContext()) {
                if (stream == null)
                    return false;

                return await SaveFileAsync(targetPath, stream, cancellationToken).AnyContext();
            }
        }

        public Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default) {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));

            EnsureClientConnected();

            try {
                _client.DeleteFile(NormalizePath(path));
            } catch (SftpPathNotFoundException ex) {
                _logger.LogDebug(ex, "Error trying to delete file: {Path}.", path);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task<int> DeleteFilesAsync(string searchPattern = null, CancellationToken cancellationToken = default) {
            var files = await GetFileListAsync(searchPattern, cancellationToken: cancellationToken).AnyContext();
            int count = 0;

            // TODO: We could batch this, but we should ensure the batch isn't thousands of files.
            foreach (var file in files) {
                await DeleteFileAsync(file.Path, cancellationToken).AnyContext();
                count++;
            }

            return count;
        }

        public async Task<PagedFileListResult> GetPagedFileListAsync(int pageSize = 100, string searchPattern = null, CancellationToken cancellationToken = default) {
            if (pageSize <= 0)
                return PagedFileListResult.Empty;

            searchPattern = NormalizePath(searchPattern);

            var result = new PagedFileListResult(r => GetFiles(searchPattern, 1, pageSize, cancellationToken));
            await result.NextPageAsync().AnyContext();
            return result;
        }

        private async Task<NextPageResult> GetFiles(string searchPattern, int page, int pageSize, CancellationToken cancellationToken) {
            int pagingLimit = pageSize;
            int skip = (page - 1) * pagingLimit;
            if (pagingLimit < Int32.MaxValue)
                pagingLimit = pagingLimit + 1;

            var list = (await GetFileListAsync(searchPattern, pagingLimit, skip, cancellationToken).AnyContext()).ToList();
            bool hasMore = false;
            if (list.Count == pagingLimit) {
                hasMore = true;
                list.RemoveAt(pagingLimit - 1);
            }

            return new NextPageResult {
                Success = true,
                HasMore = hasMore,
                Files = list,
                NextPageFunc = hasMore ? r => GetFiles(searchPattern, page + 1, pageSize, cancellationToken) : (Func<PagedFileListResult, Task<NextPageResult>>)null
            };
        }

        private async Task<IEnumerable<FileSpec>> GetFileListAsync(string searchPattern = null, int? limit = null, int? skip = null, CancellationToken cancellationToken = default) {
            if (limit.HasValue && limit.Value <= 0)
                return new List<FileSpec>();

            var list = new List<FileSpec>();
            var criteria = GetRequestCriteria(NormalizePath(searchPattern));

            EnsureClientConnected();
            if (!String.IsNullOrEmpty(criteria.Prefix) && !_client.Exists(criteria.Prefix))
                return list;

            // NOTE: This could be very expensive the larger the directory structure you have as we aren't efficiently doing paging.
            await GetFileListRecursivelyAsync(criteria.Prefix, criteria.Pattern, list).AnyContext();

            if (skip.HasValue)
                list = list.Skip(skip.Value).ToList();

            if (limit.HasValue)
                list = list.Take(limit.Value).ToList();

            return list;
        }

        private async Task GetFileListRecursivelyAsync(string prefix, Regex pattern, List<FileSpec> list) {
            var files = await Task.Factory.FromAsync(_client.BeginListDirectory(prefix, null, null), _client.EndListDirectory).AnyContext();
            foreach (var file in files) {
                if (file.IsDirectory) {
                    if (file.Name == "." || file.Name == "..")
                        continue;

                    await GetFileListRecursivelyAsync(String.Concat(prefix, "/", file.Name), pattern, list).AnyContext();
                    continue;
                }

                if (!file.IsRegularFile)
                    continue;

                string path = file.FullName.TrimStart('/');
                if (pattern != null && !pattern.IsMatch(path))
                    continue;

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
            if (!String.IsNullOrEmpty(password)) {

                authenticationMethods.Add(new PasswordAuthenticationMethod(username, password));
            }

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
            if (!_client.IsConnected)
                _client.Connect();
        }

        private void EnsureDirectoryExists(string path) {
            string directory = NormalizePath(Path.GetDirectoryName(path));
            if (String.IsNullOrEmpty(directory) || _client.Exists(directory))
                return;

            string[] folderSegments = directory.Split(new [] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string currentDirectory = String.Empty;
            foreach (string segment in folderSegments) {
                currentDirectory = String.Concat(currentDirectory, "/", segment);
                if (!_client.Exists(currentDirectory))
                    _client.CreateDirectory(currentDirectory);
            }
        }

        private string NormalizePath(string path) {
            return path?.Replace('\\', '/');
        }

        private class SearchCriteria {
            public string Prefix { get; set; }
            public Regex Pattern { get; set; }
        }

        private SearchCriteria GetRequestCriteria(string searchPattern) {
            Regex patternRegex = null;
            searchPattern = searchPattern?.Replace('\\', '/');

            string prefix = searchPattern;
            int wildcardPos = searchPattern?.IndexOf('*') ?? -1;
            if (searchPattern != null && wildcardPos >= 0) {
                patternRegex = new Regex("^" + Regex.Escape(searchPattern).Replace("\\*", ".*?") + "$");
                int slashPos = searchPattern.LastIndexOf('/');
                prefix = slashPos >= 0 ? searchPattern.Substring(0, slashPos) : String.Empty;
            }

            return new SearchCriteria {
                Prefix = prefix ?? String.Empty,
                Pattern = patternRegex
            };
        }

        public void Dispose() {
            if (_client.IsConnected)
                _client.Disconnect();

            _client.Dispose();
        }
    }
}

