using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Foundatio.Storage {
    internal static class SshNetExtensions {
        public static Task<IEnumerable<ISftpFile>> ListDirectoryAsync(this SftpClient client, string path, Action<int> listCallback = null) {
            return Task.Factory.FromAsync(client.BeginListDirectory(path, null, null, listCallback), client.EndListDirectory);
        }
    }
}
