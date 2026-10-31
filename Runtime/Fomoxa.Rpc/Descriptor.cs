using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fomoxa.Rpc
{
    public enum Shape
    {
        Unary = 0,
        ServerStream = 1,
    }

    public static class ShapeExtensions
    {
        public static bool CalleeStreams(this Shape shape) => shape == Shape.ServerStream;
    }

    public sealed record MethodPolicy
    {
        public static readonly MethodPolicy None = new MethodPolicy();

        public bool Idempotent { get; init; }

        public TimeSpan? Deadline { get; init; }

        public string? Requires { get; init; }

        public TimeSpan? Idle { get; init; }

        public bool Parallel { get; init; }
    }

    public sealed record Method(string Name, Shape Shape, uint RequestId, uint ReplyId)
    {
        public MethodPolicy Policy { get; init; } = MethodPolicy.None;

        public IReadOnlyList<uint> Details { get; init; } = Array.Empty<uint>();

        public static Method Unary(string name, uint requestId, uint responseId) =>
            new Method(name, Shape.Unary, requestId, responseId);

        public static Method ServerStream(string name, uint requestId, uint itemId) =>
            new Method(name, Shape.ServerStream, requestId, itemId);

        public Method WithPolicy(MethodPolicy policy) => this with { Policy = policy };

        public Method Requiring(string permission) => this with { Policy = Policy with { Requires = permission } };

        public Method Idempotent() => this with { Policy = Policy with { Idempotent = true } };

        public Method IdleAfter(TimeSpan idle) => this with { Policy = Policy with { Idle = idle } };

        public Method Detail(uint messageId) =>
            Details.Contains(messageId) ? this : this with { Details = Details.Append(messageId).ToArray() };

        public IEnumerable<uint> MessageIds()
        {
            yield return RequestId;
            yield return ReplyId;
        }
    }

    public sealed class UnaryMethod<TRequest, TResponse>
    {
        public UnaryMethod(string name, IRpcCodec<TRequest> request, IRpcCodec<TResponse> response, MethodPolicy? policy = null)
            : this(
                Method.Unary(name, request.MessageId, response.MessageId).WithPolicy(policy ?? MethodPolicy.None),
                request,
                response,
                Array.Empty<DetailCodec>())
        {
        }

        private UnaryMethod(Method method, IRpcCodec<TRequest> request, IRpcCodec<TResponse> response, IReadOnlyList<DetailCodec> details)
        {
            Method = method;
            Request = request;
            Response = response;
            Details = details;
        }

        public Method Method { get; }

        public IRpcCodec<TRequest> Request { get; }

        public IRpcCodec<TResponse> Response { get; }

        public IReadOnlyList<DetailCodec> Details { get; }

        public UnaryMethod<TRequest, TResponse> WithDetail<TDetail>(IRpcCodec<TDetail> codec) where TDetail : notnull =>
            new UnaryMethod<TRequest, TResponse>(
                Method.Detail(codec.MessageId),
                Request,
                Response,
                Details.Append(DetailCodec.Of(codec)).ToArray());

        public static implicit operator Method(UnaryMethod<TRequest, TResponse> descriptor) => descriptor.Method;
    }

    public sealed class ServerStreamMethod<TRequest, TItem>
    {
        public ServerStreamMethod(string name, IRpcCodec<TRequest> request, IRpcCodec<TItem> item, MethodPolicy? policy = null)
            : this(
                Method.ServerStream(name, request.MessageId, item.MessageId).WithPolicy(policy ?? MethodPolicy.None),
                request,
                item,
                Array.Empty<DetailCodec>())
        {
        }

        private ServerStreamMethod(Method method, IRpcCodec<TRequest> request, IRpcCodec<TItem> item, IReadOnlyList<DetailCodec> details)
        {
            Method = method;
            Request = request;
            Item = item;
            Details = details;
        }

        public Method Method { get; }

        public IRpcCodec<TRequest> Request { get; }

        public IRpcCodec<TItem> Item { get; }

        public IReadOnlyList<DetailCodec> Details { get; }

        public ServerStreamMethod<TRequest, TItem> WithDetail<TDetail>(IRpcCodec<TDetail> codec) where TDetail : notnull =>
            new ServerStreamMethod<TRequest, TItem>(
                Method.Detail(codec.MessageId),
                Request,
                Item,
                Details.Append(DetailCodec.Of(codec)).ToArray());

        public static implicit operator Method(ServerStreamMethod<TRequest, TItem> descriptor) => descriptor.Method;
    }

    public sealed class Entry
    {
        public Entry(Method method, Handler? handler)
        {
            Method = method;
            Handler = handler;
        }

        public Method Method { get; }

        public Handler? Handler { get; }
    }

    public sealed class RegistryException : Exception
    {
        public RegistryException(string message) : base(message)
        {
        }
    }

    public sealed class Registry
    {
        public const int MaxMethods = 65_536;

        private readonly List<Entry> entries;
        private readonly Dictionary<uint, Entry> byRequest;
        private readonly HashSet<uint> idSet;
        private readonly HashSet<uint> plainIds;
        private readonly HashSet<uint> detailsIds;
        private readonly HashSet<uint> exempt;

        internal Registry(
            uint voidId,
            uint errorId,
            List<Entry> entries,
            Dictionary<uint, Entry> byRequest,
            HashSet<uint> idSet,
            HashSet<uint> plainIds,
            HashSet<uint> detailsIds,
            uint? authentication,
            HashSet<uint> exempt,
            Health? health)
        {
            Health = health;
            Authentication = authentication;
            this.exempt = exempt;
            VoidId = voidId;
            ErrorId = errorId;
            this.entries = entries;
            this.byRequest = byRequest;
            this.idSet = idSet;
            this.plainIds = plainIds;
            this.detailsIds = detailsIds;
        }

        public static RegistryBuilder Builder(uint voidId, uint errorId) => new RegistryBuilder(voidId, errorId);

        public uint VoidId { get; }

        public uint ErrorId { get; }

        public uint? Authentication { get; }

        public IReadOnlyCollection<uint> Exempt => exempt;

        public Health? Health { get; }

        public IEnumerable<Method> Methods => entries.Select(entry => entry.Method);

        public IReadOnlyCollection<uint> FrpcIds => idSet;

        public bool Owns(uint messageId) => idSet.Contains(messageId);

        public bool IsPlain(uint messageId) => plainIds.Contains(messageId);

        public bool DeclaresDetails(uint messageId) => detailsIds.Contains(messageId);

        public Entry? EntryFor(uint requestId) => byRequest.TryGetValue(requestId, out var entry) ? entry : null;

        public Method? MethodFor(uint requestId) => EntryFor(requestId)?.Method;

        public void RequireDeclaredIn(Func<uint, bool> schemaDeclares)
        {
            foreach (var entry in entries)
            {
                foreach (uint messageId in entry.Method.MessageIds())
                {
                    if (!schemaDeclares(messageId))
                    {
                        throw new RegistryException(
                            $"method {entry.Method.Name} uses 0x{messageId:X8}, which the schema does not declare");
                    }
                }
            }
            foreach (uint messageId in detailsIds)
            {
                if (!schemaDeclares(messageId))
                {
                    throw new RegistryException($"details model 0x{messageId:X8} is not declared in the schema");
                }
            }
        }
    }

    public sealed class RegistryBuilder
    {
        private readonly uint voidId;
        private readonly uint errorId;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly HashSet<uint> plainIds = new HashSet<uint>();
        private readonly List<Method> authenticators = new List<Method>();
        private readonly HashSet<uint> exempt = new HashSet<uint>();
        private readonly HashSet<uint> guarded = new HashSet<uint>();
        private (uint Request, uint Response)? health;

        internal RegistryBuilder(uint voidId, uint errorId)
        {
            this.voidId = voidId;
            this.errorId = errorId;
        }

        public RegistryBuilder Declare(Method method)
        {
            entries.Add(new Entry(method, null));
            return this;
        }

        public RegistryBuilder Serve(Method method, Handler handler)
        {
            entries.Add(new Entry(method, handler));
            return this;
        }

        public RegistryBuilder Serve<TRequest, TResponse>(
            UnaryMethod<TRequest, TResponse> method,
            Func<CallContext, TRequest, TResponse> handler) =>
            Serve(method.Method, Decoded(method.Method, method.Request, method.Details, (responder, request) =>
                Answer.Respond(method.Response.Encode(handler(responder.Context, request)))));

        public RegistryBuilder Serve<TRequest, TResponse>(
            UnaryMethod<TRequest, TResponse> method,
            Action<Reply<TResponse>, TRequest> handler) =>
            Serve(method.Method, Decoded(method.Method, method.Request, method.Details, (responder, request) =>
            {
                handler(new Reply<TResponse>(responder, method.Response, method.Details), request);
                return Answer.Later;
            }));

        public RegistryBuilder Serve<TRequest, TResponse>(
            UnaryMethod<TRequest, TResponse> method,
            Func<CallContext, TRequest, Task<TResponse>> handler) =>
            Serve(method.Method, Decoded(method.Method, method.Request, method.Details, (responder, request) =>
                Answer.Await(Encoded(handler(responder.Context, request), method.Response, method.Details))));

        public RegistryBuilder Serve<TRequest, TItem>(
            ServerStreamMethod<TRequest, TItem> method,
            Action<ItemStream<TItem>, TRequest> handler) =>
            Serve(method.Method, Decoded(method.Method, method.Request, method.Details, (responder, request) =>
            {
                handler(new ItemStream<TItem>(responder, method.Item, method.Details), request);
                return Answer.Later;
            }));

        public RegistryBuilder Authenticate<TRequest, TResponse>(IAuthenticator<TRequest, TResponse> authenticator)
        {
            var method = authenticator.Method;
            authenticators.Add(method.Method);
            return Serve(method.Method, Decoded(method.Method, method.Request, method.Details, (responder, request) =>
            {
                if (responder.Context.Session != null)
                {
                    return Answer.Fail(Status.FailedPrecondition("the session is already authenticated"));
                }
                var (identity, response) = authenticator.Authenticate(responder.Context, request);
                responder.Authenticate(identity);
                return Answer.Respond(method.Response.Encode(response));
            }));
        }

        public RegistryBuilder Exempting(uint requestId)
        {
            exempt.Add(requestId);
            return this;
        }

        public RegistryBuilder RequiringIdentity(uint requestId)
        {
            guarded.Add(requestId);
            return this;
        }

        public RegistryBuilder Health(uint requestId, uint responseId)
        {
            health = (requestId, responseId);
            return this;
        }

        public RegistryBuilder PlainMessage(uint messageId)
        {
            plainIds.Add(messageId);
            return this;
        }

        public Registry Build()
        {
            var all = new List<Entry>(entries);
            var allExempt = new HashSet<uint>(exempt);
            Health? serving = null;
            if (health is (uint Request, uint Response) ids)
            {
                serving = new Health(Rpc.Health.ServicesOf(all));
                all.Add(new Entry(Method.Unary(HealthStatus.Method, ids.Request, ids.Response), serving.Handler()));
                allExempt.Add(ids.Request);
            }
            allExempt.ExceptWith(guarded);
            if (all.Count > Registry.MaxMethods)
            {
                throw new RegistryException($"{all.Count} methods exceeds the {Registry.MaxMethods} ceiling");
            }
            if (authenticators.Count > 1)
            {
                throw new RegistryException(
                    $"{authenticators[1].Name} is a second authenticator; {authenticators[0].Name} already authenticates");
            }

            var byRequest = new Dictionary<uint, Entry>();
            var idSet = new HashSet<uint> { voidId, errorId };
            var detailsIds = new HashSet<uint>();

            foreach (var entry in all)
            {
                var method = entry.Method;
                if (byRequest.TryGetValue(method.RequestId, out var previous))
                {
                    throw new RegistryException(
                        $"methods {previous.Method.Name} and {method.Name} share request type 0x{method.RequestId:X8}");
                }
                byRequest.Add(method.RequestId, entry);
                foreach (uint messageId in method.MessageIds())
                {
                    if (plainIds.Contains(messageId))
                    {
                        throw new RegistryException(
                            $"method {method.Name} uses 0x{messageId:X8}, which is also a plain message id");
                    }
                    bool voidResponse = messageId == method.ReplyId && method.Shape == Shape.Unary;
                    if (messageId == errorId || (messageId == voidId && !voidResponse))
                    {
                        throw new RegistryException(
                            $"method {method.Name} uses 0x{messageId:X8}, reserved for a system model");
                    }
                    idSet.Add(messageId);
                }
                detailsIds.UnionWith(method.Details);
            }

            return new Registry(
                voidId,
                errorId,
                all,
                byRequest,
                idSet,
                new HashSet<uint>(plainIds),
                detailsIds,
                authenticators.Count == 0 ? (uint?)null : authenticators[0].RequestId,
                allExempt,
                serving);
        }

        private static Handler Decoded<TRequest>(
            Method method, IRpcCodec<TRequest> codec, IReadOnlyList<DetailCodec> details, Func<Responder, TRequest, Answer> handle)
        {
            bool parallel = method.Policy.Parallel;
            Answer run(Responder responder, TRequest request)
            {
                try
                {
                    var answer = handle(responder, request);
                    return answer.Kind == AnswerKind.Fail ? Answer.Fail(answer.Status!.EncodedWith(details)) : answer;
                }
                catch (RpcException refused)
                {
                    return Answer.Fail(refused.Status.EncodedWith(details));
                }
            }
            return (responder, body) =>
            {
                TRequest request;
                try
                {
                    request = codec.Decode(body);
                }
                catch (Exception error)
                {
                    return Answer.Fail(Status.InvalidArgument($"{method.Name} request does not decode: {error.Message}"));
                }
                return parallel ? Answer.Run(() => run(responder, request)) : run(responder, request);
            };
        }

        private static Task<byte[]> Encoded<TResponse>(Task<TResponse> task, IRpcCodec<TResponse> codec, IReadOnlyList<DetailCodec> details) =>
            task.ContinueWith(
                completed =>
                {
                    try
                    {
                        return codec.Encode(completed.GetAwaiter().GetResult());
                    }
                    catch (RpcException refused)
                    {
                        throw new RpcException(refused.Status.EncodedWith(details));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}
