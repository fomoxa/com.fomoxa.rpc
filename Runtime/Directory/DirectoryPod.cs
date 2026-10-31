using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fomoxa.Net;
using Fomoxa.Net.Transports;
using Fomoxa.Networking;
using Fomoxa.Networking.Transports;
using Fomoxa.Rpc.Net;
using FomoxaDirectoryCodecs;

namespace Fomoxa.Rpc.Directory
{
    public sealed class DirectoryPod : IDisposable
    {
        private readonly DirectoryPodSettings settings;
        private readonly Func<int> players;
        private readonly Action stopServer;
        private readonly Func<TimeSpan> clock;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly TimeManager? timeManager;
        private readonly Action tick;
        private readonly Schema schema = NetSchema.Build();
        private readonly Registry registry = DirectoryClient.Declare(DirectoryAuthenticationClient.Registry()).Build();
        private readonly HashSet<string> rooms = new HashSet<string>();
        private readonly HashSet<string> acquiring = new HashSet<string>();
        private Phase phase = Phase.Stopped;
        private ITransportConnector? connector;
        private TimeSpan connectingSince;
        private RpcConnection? connection;
        private Task<PodRegistered>? registering;
        private uint registeringAs;
        private TimeSpan retryAt;
        private int registration;
        private TimeSpan reportInterval;
        private TimeSpan nextReport;
        private Task? reporting;
        private RpcCall? drainWatch;
        private TimeSpan drainWatchAt;
        private bool deadlineReached;

        public DirectoryPod(NetworkRuntime runtime, DirectoryPodSettings settings)
            : this(runtime.ServerManager, runtime.TimeManager, settings)
        {
        }

        public DirectoryPod(ServerManager server, TimeManager time, DirectoryPodSettings settings)
            : this(
                settings,
                () => server.PeerCount,
                (server ?? throw new ArgumentNullException(nameof(server))).StopConnection,
                () => MonotonicClock.Now,
                () => DateTimeOffset.UtcNow,
                time ?? throw new ArgumentNullException(nameof(time)))
        {
        }

        internal DirectoryPod(DirectoryPodSettings settings, Func<int> players, Action stopServer, Func<TimeSpan> clock, Func<DateTimeOffset> utcNow, TimeManager? timeManager = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.players = players;
            this.stopServer = stopServer;
            this.clock = clock;
            this.utcNow = utcNow;
            this.timeManager = timeManager;
            tick = Tick;
        }

        public event Action<DirectoryRegistration>? OnRegistered;

        public event Action<DateTimeOffset>? OnDrain;

        public event Action? OnDrainDeadline;

        public event Action<Exception>? OnError;

        private enum Phase
        {
            Stopped,
            Waiting,
            Connecting,
            Opening,
            Registering,
            Registered,
        }

        public bool IsRunning => phase != Phase.Stopped;

        public bool IsRegistered => phase == Phase.Registered;

        public uint PodId { get; private set; }

        public byte[] TicketKey { get; private set; } = Array.Empty<byte>();

        public IReadOnlyCollection<string> Rooms => rooms;

        public DateTimeOffset? DrainDeadline { get; private set; }

        public bool IsDraining => DrainDeadline != null;

        public Func<float>? Load { get; set; }

        public void Start()
        {
            if (phase != Phase.Stopped)
            {
                return;
            }

            settings.Validate();
            phase = Phase.Waiting;
            retryAt = clock();
            if (timeManager != null)
            {
                timeManager.OnPostTick += tick;
            }
        }

        public void Stop()
        {
            if (phase == Phase.Stopped)
            {
                return;
            }

            if (timeManager != null)
            {
                timeManager.OnPostTick -= tick;
            }

            Disconnect();
            phase = Phase.Stopped;
        }

        public void Dispose() => Stop();

        public Task AcquireRoom(string roomKey)
        {
            if (string.IsNullOrEmpty(roomKey))
            {
                throw new ArgumentException("the room key is empty", nameof(roomKey));
            }

            if (connection == null || phase != Phase.Registered)
            {
                return NotRegistered<FRpcVoid>();
            }

            acquiring.Add(roomKey);
            return Acquired(connection.Unary(DirectoryMethods.AcquireRoom, new AcquireRoom { RoomKey = roomKey }), roomKey, registration);
        }

        public Task ReleaseRoom(string roomKey)
        {
            if (string.IsNullOrEmpty(roomKey))
            {
                throw new ArgumentException("the room key is empty", nameof(roomKey));
            }

            rooms.Remove(roomKey);
            acquiring.Remove(roomKey);
            if (connection == null || phase != Phase.Registered)
            {
                return Task.CompletedTask;
            }

            return connection.Unary(DirectoryMethods.ReleaseRoom, new ReleaseRoom { RoomKey = roomKey });
        }

        public Task RequestDrain()
        {
            if (connection == null || phase != Phase.Registered)
            {
                return NotRegistered<FRpcVoid>();
            }

            return connection.Unary(DirectoryMethods.RequestDrain, new DrainSelf());
        }

        public Task<AllocateResponse> Relocate(string roomKey, uint tickets, byte[] applicationData)
        {
            if (string.IsNullOrEmpty(roomKey))
            {
                throw new ArgumentException("the room key is empty", nameof(roomKey));
            }

            if (connection == null || phase != Phase.Registered)
            {
                return NotRegistered<AllocateResponse>();
            }

            return connection.Unary(DirectoryMethods.Relocate, new RelocateRoom
            {
                RoomKey = roomKey,
                Tickets = tickets,
                ApplicationData = applicationData ?? Array.Empty<byte>(),
            });
        }

        internal void Tick()
        {
            if (phase == Phase.Stopped)
            {
                return;
            }

            TimeSpan now = clock();
            connection?.Tick(now);
            switch (phase)
            {
                case Phase.Waiting:
                    if (now >= retryAt)
                    {
                        BeginConnect(now);
                    }

                    break;
                case Phase.Connecting:
                    PollConnector(now);
                    break;
                case Phase.Opening:
                    PollOpened(now);
                    break;
                case Phase.Registering:
                    PollRegistering(now);
                    break;
                case Phase.Registered:
                    PollRegistered(now);
                    break;
            }

            CheckDrainDeadline();
        }

        private static Task<T> NotRegistered<T>() =>
            Task.FromException<T>(new RpcException(new Status(StatusCode.Unavailable, "the pod is not registered with the directory")));

        private async Task Acquired(Task<FRpcVoid> call, string roomKey, int madeIn)
        {
            try
            {
                await call;
            }
            catch
            {
                acquiring.Remove(roomKey);
                throw;
            }

            if (madeIn != registration || phase != Phase.Registered)
            {
                acquiring.Remove(roomKey);
                throw new RpcException(new Status(StatusCode.Unavailable, "the connection to the directory ended before its answer was applied"));
            }

            if (acquiring.Remove(roomKey))
            {
                rooms.Add(roomKey);
            }
        }

        private void BeginConnect(TimeSpan now)
        {
            try
            {
                connector = settings.Transport.CreateConnector(settings.DirectoryAddress, settings.DirectoryPort);
            }
            catch (Exception exception)
            {
                Fail(now, exception);
                return;
            }

            connectingSince = now;
            phase = Phase.Connecting;
            PollConnector(now);
        }

        private void PollConnector(TimeSpan now)
        {
            ConnectStatus status;
            ITransport transport;
            try
            {
                status = connector!.Poll(out transport);
            }
            catch (Exception exception)
            {
                Fail(now, exception);
                return;
            }

            switch (status)
            {
                case ConnectStatus.Connected:
                    connector!.Dispose();
                    connector = null;
                    Open(now, transport);
                    break;
                case ConnectStatus.Pending:
                    if (now - connectingSince > settings.ConnectTimeout)
                    {
                        Fail(now, new TimeoutException($"the directory at {settings.DirectoryAddress}:{settings.DirectoryPort} did not answer within {settings.ConnectTimeout}"));
                    }

                    break;
                default:
                    Fail(now, new InvalidOperationException($"could not connect to the directory at {settings.DirectoryAddress}:{settings.DirectoryPort} ({status})"));
                    break;
            }
        }

        private void Open(TimeSpan now, ITransport transport)
        {
            var options = new ConnectOptions
            {
                Timeout = settings.ConnectTimeout,
                Credential = Credential.Of(DirectoryAuthenticationMethods.Authenticate, new DirectoryAuth { Role = DirectoryRoles.Pod, Key = settings.Key }),
            };
            try
            {
                connection = RpcConnection.Driven(transport, schema, registry, options);
            }
            catch (Exception exception)
            {
                transport.Dispose();
                Fail(now, exception);
                return;
            }

            phase = Phase.Opening;
            connection.Tick(now);
            PollOpened(now);
        }

        private void PollOpened(TimeSpan now)
        {
            Task<RpcConnection> opened = connection!.Opened;
            if (!opened.IsCompleted)
            {
                return;
            }

            if (opened.Status != TaskStatus.RanToCompletion)
            {
                Fail(now, Unwrap(opened.Exception));
                return;
            }

            Register(PodId);
        }

        private void Register(uint podId)
        {
            registeringAs = podId;
            var request = new RegisterPod
            {
                PodId = podId,
                Address = settings.Address,
                Port = settings.Port,
                Target = settings.Target,
                Version = settings.Version,
                Region = settings.Region,
                Labels = settings.Labels
                    .OrderBy(label => label.Key, StringComparer.Ordinal)
                    .Select(label => new DirectoryLabel { Name = label.Key, Value = label.Value })
                    .ToList(),
                MaxPlayers = settings.MaxPlayers,
                Rooms = rooms.OrderBy(room => room, StringComparer.Ordinal).ToList(),
            };
            registering = connection!.Unary(DirectoryMethods.RegisterPod, request);
            phase = Phase.Registering;
        }

        private void PollRegistering(TimeSpan now)
        {
            Task<PodRegistered> answer = registering!;
            if (!answer.IsCompleted)
            {
                return;
            }

            registering = null;
            if (answer.Status != TaskStatus.RanToCompletion)
            {
                Exception failure = Unwrap(answer.Exception);
                if (registeringAs != 0 && failure is RpcException { Status: { Code: StatusCode.FailedPrecondition } } && connection!.IsOpen)
                {
                    Register(0);
                    return;
                }

                Fail(now, failure);
                return;
            }

            PodRegistered registered = answer.Result;
            uint previous = PodId;
            PodId = registered.PodId;
            TicketKey = registered.TicketKey;
            reportInterval = TimeSpan.FromMilliseconds(Math.Max(1u, registered.ReportIntervalMs));
            nextReport = now;
            drainWatchAt = now;
            registration++;
            phase = Phase.Registered;
            OnRegistered?.Invoke(new DirectoryRegistration(PodId, previous, TicketKey));
            PollRegistered(now);
        }

        private void PollRegistered(TimeSpan now)
        {
            if (!connection!.IsOpen)
            {
                Fail(now, new InvalidOperationException("the connection to the directory ended"));
                return;
            }

            PollDrainWatch(now);
            if (reporting != null && reporting.IsCompleted)
            {
                _ = reporting.Exception;
                reporting = null;
            }

            if (now >= nextReport && reporting == null)
            {
                reporting = connection.Unary(DirectoryMethods.ReportLoad, CurrentLoad());
                nextReport = now + reportInterval;
            }
        }

        private PodLoad CurrentLoad()
        {
            int peers = Math.Max(0, players());
            float load = Load?.Invoke() ?? (float)peers / settings.MaxPlayers;
            if (float.IsNaN(load))
            {
                load = 1f;
            }

            return new PodLoad
            {
                Players = (uint)peers,
                Rooms = (uint)rooms.Count,
                Load = Math.Min(1f, Math.Max(0f, load)),
            };
        }

        private void PollDrainWatch(TimeSpan now)
        {
            if (drainWatch == null)
            {
                if (now < drainWatchAt)
                {
                    return;
                }

                drainWatch = connection!.Call(
                    DirectoryMethods.WatchDrain.Method.RequestId,
                    DirectoryMethods.WatchDrain.Request.Encode(new DrainWatch()),
                    CallOptions.WithoutDeadline);
            }

            while (drainWatch.TryNext(out CallEvent next))
            {
                Outcome? outcome = next.Outcome;
                if (outcome != null && outcome.Kind == OutcomeKind.Item)
                {
                    Drain(DirectoryMethods.WatchDrain.Item.Decode(outcome.Body).Deadline);
                    continue;
                }

                drainWatch = null;
                drainWatchAt = now + settings.RetryDelay;
                return;
            }
        }

        private void Drain(long deadlineUnixMs)
        {
            DateTimeOffset deadline = DateTimeOffset.FromUnixTimeMilliseconds(deadlineUnixMs);
            if (DrainDeadline == deadline)
            {
                return;
            }

            DrainDeadline = deadline;
            OnDrain?.Invoke(deadline);
        }

        private void CheckDrainDeadline()
        {
            if (deadlineReached || DrainDeadline == null || utcNow() < DrainDeadline.Value)
            {
                return;
            }

            deadlineReached = true;
            stopServer();
            OnDrainDeadline?.Invoke();
        }

        private void Fail(TimeSpan now, Exception reason)
        {
            Disconnect();
            phase = Phase.Waiting;
            retryAt = now + settings.RetryDelay;
            OnError?.Invoke(reason);
        }

        private void Disconnect()
        {
            connector?.Dispose();
            connector = null;
            connection?.Dispose();
            connection = null;
            registering = null;
            reporting = null;
            drainWatch = null;
        }

        private static Exception Unwrap(AggregateException? exception) =>
            exception?.InnerExceptions.Count == 1 ? exception.InnerException! : (Exception?)exception ?? new InvalidOperationException("the call ended without a result");
    }
}
