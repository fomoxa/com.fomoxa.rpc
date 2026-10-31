using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Fomoxa.Rpc.Tests
{
    internal sealed class GatewayProcess : IDisposable
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
        private static readonly Regex Started = new Regex(@"fomoxa-gateway: clients on UDP ");

        private readonly Process process;
        private readonly string folder;

        private readonly StringBuilder lines;

        private GatewayProcess(Process process, string folder, StringBuilder lines)
        {
            this.process = process;
            this.folder = folder;
            this.lines = lines;
        }

        public string Output
        {
            get
            {
                lock (lines)
                {
                    return lines.ToString();
                }
            }
        }

        public static GatewayProcess Start(ushort clientPort, DirectoryProcess directory, ushort webSocketPort = 0)
        {
            string binary = DirectoryProcess.Binary("fomoxa-gateway", "FomoxaGateway");
            string folder = Path.Combine(Path.GetTempPath(), "fomoxa-gateway-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            string config = Path.Combine(folder, "gateway.toml");
            File.WriteAllText(config, $@"listen = ""127.0.0.1:{clientPort}""
pod_bind = ""127.0.0.1:0""
directory = ""{directory.Address}""
retry_seconds = 1
{(webSocketPort == 0 ? "" : $"websocket_listen = \"127.0.0.1:{webSocketPort}\"\nwebsocket_path = \"/game\"")}

[keys]
gateway = {{ env = ""FOMOXA_TEST_GATEWAY_KEY"" }}
");
            var start = new ProcessStartInfo(binary, $"\"{config}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.Environment["FOMOXA_TEST_GATEWAY_KEY"] = DirectoryProcess.GatewayKey;

            var process = new Process { StartInfo = start };
            var lines = new StringBuilder();
            bool started = false;
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

                if (Started.IsMatch(args.Data))
                {
                    started = true;
                    ready.Set();
                }
            };
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (!ready.Wait(StartTimeout) || !started)
            {
                DirectoryProcess.Kill(process);
                System.IO.Directory.Delete(folder, true);
                lock (lines)
                {
                    throw new InvalidOperationException($"fomoxa-gateway did not start: {lines}");
                }
            }

            return new GatewayProcess(process, folder, lines);
        }

        public void Dispose()
        {
            DirectoryProcess.Kill(process);
            process.Dispose();
            try
            {
                System.IO.Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
