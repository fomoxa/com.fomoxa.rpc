using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Fomoxa.Rpc.Net
{
    public interface IReadiness
    {
        bool ReportsWritable { get; }

        bool WaitReady(bool writable, TimeSpan timeout, Waker waker);
    }

    public sealed class Waker : IDisposable
    {
        private static readonly byte[] Signal = { 1 };

        private readonly Socket socket;
        private readonly byte[] scratch = new byte[64];

        public Waker()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            socket.Connect(socket.LocalEndPoint!);
            socket.Blocking = false;
        }

        public Socket Socket => socket;

        public void Wake()
        {
            try
            {
                socket.Send(Signal);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public bool Clear()
        {
            bool woken = false;
            try
            {
                while (socket.Available > 0)
                {
                    socket.Receive(scratch);
                    woken = true;
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            return woken;
        }

        public bool Sleep(TimeSpan timeout)
        {
            try
            {
                return socket.Poll(Micros(timeout), SelectMode.SelectRead);
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        public void Dispose() => socket.Dispose();

        internal static int Micros(TimeSpan timeout) => (int)Math.Clamp(timeout.Ticks / 10, 0, int.MaxValue);
    }

    public sealed class SocketReadiness : IReadiness
    {
        private readonly Socket socket;

        public SocketReadiness(Socket socket)
        {
            this.socket = socket;
        }

        public bool ReportsWritable => true;

        public bool WaitReady(bool writable, TimeSpan timeout, Waker waker)
        {
            try
            {
                var readers = new List<Socket> { socket, waker.Socket };
                var writers = writable ? new List<Socket> { socket } : null;
                var failures = new List<Socket> { socket };
                Socket.Select(readers, writers, failures, Waker.Micros(timeout));
                return readers.Contains(socket) || writers?.Count > 0 || failures.Count > 0;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
