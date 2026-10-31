using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fomoxa.Rpc.Directory;
using Fomoxa.Rpc.Net;
using FomoxaDirectoryCodecs;
using NUnit.Framework;

namespace Fomoxa.Rpc.Tests
{
    internal sealed class DirectoryProcess : IDisposable
    {
        public const string BackendKey = "backend-key-for-directory-tests";
        public const string PodKey = "pod-key-for-directory-tests";
        public const string GatewayKey = "gateway-key-for-directory-tests";
        public const string AdminKey = "admin-key-for-directory-tests";
        public static readonly byte[] RootKey = Enumerable.Range(0x40, 32).Select(value => (byte)value).ToArray();

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        private static readonly Regex Listening = new Regex(@"Directory on (\S+):(\d+), DirectoryAdmin on (\S+):(\d+)");

        private readonly Process process;
        private readonly string folder;
        private RpcConnection? admin;

        private DirectoryProcess(Process process, string folder, ushort port, string adminAddress)
        {
            this.process = process;
            this.folder = folder;
            Port = port;
            AdminAddress = adminAddress;
        }

        public ushort Port { get; }

        public string Address => $"127.0.0.1:{Port}";

        public string AdminAddress { get; }

        public static DirectoryProcess Start(ushort port = 0, int drainSeconds = 300)
        {
            string binary = Binary();
            string folder = Path.Combine(Path.GetTempPath(), "fomoxa-directory-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            string config = Path.Combine(folder, "directory.toml");
            File.WriteAllText(config, $@"listen = ""127.0.0.1:{port}""
admin_listen = ""127.0.0.1:0""
grace_seconds = 1
report_interval_seconds = 1
drain_seconds = {drainSeconds}
ticket_seconds = 30

[keys]
backend = {{ env = ""FOMOXA_TEST_BACKEND_KEY"" }}
pod = {{ env = ""FOMOXA_TEST_POD_KEY"" }}
gateway = {{ env = ""FOMOXA_TEST_GATEWAY_KEY"" }}
admin = {{ env = ""FOMOXA_TEST_ADMIN_KEY"" }}
root = {{ env = ""FOMOXA_TEST_ROOT_KEY"" }}
");
            var start = new ProcessStartInfo(binary, $"\"{config}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.Environment["FOMOXA_TEST_BACKEND_KEY"] = BackendKey;
            start.Environment["FOMOXA_TEST_POD_KEY"] = PodKey;
            start.Environment["FOMOXA_TEST_GATEWAY_KEY"] = GatewayKey;
            start.Environment["FOMOXA_TEST_ADMIN_KEY"] = AdminKey;
            start.Environment["FOMOXA_TEST_ROOT_KEY"] = string.Concat(RootKey.Select(value => value.ToString("x2")));

            var process = new Process { StartInfo = start };
            var lines = new StringBuilder();
            Match? listening = null;
            var ready = new ManualResetEventSlim();
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data == null)
                {
                    ready.Set();
                    return;
                }

                lock (lines)
                {
                    lines.AppendLine(args.Data);
                }

                Match match = Listening.Match(args.Data);
                if (match.Success)
                {
                    listening = match;
                    ready.Set();
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (!ready.Wait(StartTimeout) || listening == null)
            {
                Kill(process);
                System.IO.Directory.Delete(folder, true);
                lock (lines)
                {
                    throw new InvalidOperationException($"fomoxa-directory did not start: {lines}");
                }
            }

            return new DirectoryProcess(process, folder, ushort.Parse(listening.Groups[2].Value), $"127.0.0.1:{listening.Groups[4].Value}");
        }

        public static ushort FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ushort port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public RpcConnection Backend() => Connect(Address, DirectoryRoles.Backend, BackendKey);

        public RpcConnection Admin() => Connect(AdminAddress, DirectoryRoles.Admin, AdminKey);

        public RpcConnection PodConnection() => Connect(Address, DirectoryRoles.Pod, PodKey);

        public List<PodState> Pods()
        {
            admin ??= Admin();
            Task<PodList> read = new DirectoryAdminClient(admin).ReadPodsAsync(new ReadPods());
            Assert.IsTrue(read.Wait(StartTimeout), "ReadPods did not finish in time");
            return read.Result.Pods;
        }

        public void Dispose()
        {
            admin?.Dispose();
            admin = null;
            Kill(process);
            process.Dispose();
            try
            {
                System.IO.Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static RpcConnection Connect(string address, string role, string key)
        {
            var registry = DirectoryAdminClient.Declare(DirectoryClient.Declare(DirectoryAuthenticationClient.Registry())).Build();
            var options = new ConnectOptions
            {
                Credential = Credential.Of(DirectoryAuthenticationMethods.Authenticate, new DirectoryAuth { Role = role, Key = Encoding.UTF8.GetBytes(key) }),
            };
            return RpcConnection.Connect(address, NetSchema.Build(), registry, options).GetAwaiter().GetResult();
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static string Binary()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.OSArchitecture != Architecture.X64)
            {
                Assert.Ignore("the fomoxa-directory binary of the tests is built for Linux x64");
            }

            string? path = Located();
            if (path == null || !File.Exists(path))
            {
                Assert.Ignore($"the fomoxa-directory binary is not here ({path ?? "no path"}); run Tools/sync-directory.sh");
            }

            return path!;
        }

        private static string? Located()
        {
            foreach (AssemblyMetadataAttribute attribute in typeof(DirectoryProcess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key == "FomoxaDirectory")
                {
                    return attribute.Value;
                }
            }
#if UNITY_EDITOR
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DirectoryPod).Assembly);
            if (package != null)
            {
                return Path.GetFullPath(Path.Combine(package.resolvedPath, "..", "rpc-tests", "directory", "fomoxa-directory"));
            }
#endif
            return null;
        }
    }
}
