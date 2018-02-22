using System;
using System.IO;
using System.Text;
using Renci.SshNet;

namespace Foundatio.Storage {
    public class SshNetFileStorageOptions : SharedOptions {
        public string ConnectionString { get; set; }
        public string Proxy { get; set; }
        public ProxyTypes ProxyType { get; set; }
        public Stream PrivateKey { get; set; }
        public string PrivateKeyPassPhrase { get; set; }
    }

    public class SshNetFileStorageOptionsBuilder : SharedOptionsBuilder<SshNetFileStorageOptions, SshNetFileStorageOptionsBuilder> {
        public SshNetFileStorageOptionsBuilder ConnectionString(string connectionString) {
            Target.ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            return this;
        }
        
        public SshNetFileStorageOptionsBuilder Proxy(string proxy) {
            Target.Proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
            return this;
        }
        
        public SshNetFileStorageOptionsBuilder ProxyType(ProxyTypes proxyType) {
            Target.ProxyType = proxyType;
            return this;
        }
        
        
        public SshNetFileStorageOptionsBuilder PrivateKey(string privateKey) {
            if (String.IsNullOrEmpty(privateKey)) 
                throw new ArgumentNullException(nameof(privateKey));

            Target.PrivateKey = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
            return this;
        }
        
        public SshNetFileStorageOptionsBuilder PrivateKey(Stream privateKey) {
            Target.PrivateKey = privateKey ?? throw new ArgumentNullException(nameof(privateKey));
            return this;
        }
        
        public SshNetFileStorageOptionsBuilder PrivateKeyPassPhrase(string privateKeyPassPhrase) {
            Target.PrivateKeyPassPhrase = privateKeyPassPhrase ?? throw new ArgumentNullException(nameof(privateKeyPassPhrase));
            return this;
        }
    }
}
