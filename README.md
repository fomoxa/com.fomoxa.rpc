# Fomoxa RPC for Unity

`com.fomoxa.rpc` brings fRPC, the RPC layer of the Fomoxa system, to Unity. A Unity client calls the game's backend through it, and a Unity game server (a pod) calls the directory. It runs on the transports of `com.fomoxa.networking`, so TLS over TCP, `wss://` and the browser's WebSocket in WebGL all work. Projects that do not need fRPC do not install it; `com.fomoxa.networking` does not depend on it.

| | |
|---|---|
| Version | 0.1.2 |
| Needs | `com.fomoxa.networking` 0.3.1 |
| Unity | 6000.5 or later |
| License | Apache-2.0 |

## Installation

Install `com.fomoxa.networking` first, then open Window > Package Manager, click +, choose Install package from git URL and enter:

```
https://github.com/fomoxa/com.fomoxa.rpc.git
```

The Unity Package Manager does not accept a Git URL as a dependency, so the package checks the version of `com.fomoxa.networking` when it generates code and reports a mismatch as an error.

## Declaring a service

Models are declared as for `com.fomoxa.networking`, with an `rpc` codec, in the Models Folder of the Fomoxa settings asset:

```csharp
using Fomoxa;

[Network]
[Codec("rpc")]
public class EchoRequest
{
    [Network("string")]
    [Codec("rpc")]
    public string Text { get; set; } = "";
}
```

A service is an interface marked `[Service]`:

```csharp
using Fomoxa.Rpc;

[Service("Echo")]
public interface IEcho
{
    [Unary]
    EchoReply Say(EchoRequest request);

    [Unary]
    void Ping();
}
```

The service must be in the same assembly as the Generated Folder of the settings asset, and that assembly must reference `Fomoxa.Rpc`. A source generator in this package turns the interface into `EchoMethods`, `EchoClient` and `EchoServer`.

A method without a request, such as `Ping`, sends an empty model that you do not write. The first compilation after you add such a method fails with `FRPC003`; the package then writes the model into `FRpcModels/` of the Generated Folder, runs `fomoxac`, and Unity compiles again. The package also copies its own system models to `FomoxaRpcSystemModels/` at the project root, outside `Assets/`. Commit both folders with the rest of the generated code.

## Calling

`RpcConnection.Driven` runs fRPC on the application's loop, with no thread of its own. Create the transport with a factory of `com.fomoxa.networking`, for example `new TcpTransportFactory(tlsSettings)`, then tick the connection every frame:

```csharp
ITransportConnector connector = factory.CreateConnector(address, port);
// when connector.Poll(out ITransport transport) returns ConnectStatus.Connected:
var connection = RpcConnection.Driven(transport, NetSchema.Build(), EchoClient.Registry().Build());
var echo = new EchoClient(connection);
EchoReply reply = await echo.SayAsync(new EchoRequest { Text = "hi" });
```

`connection.Tick(now)` must run every frame; the awaited call completes inside it, on the main thread.

## Running as a pod

A game server that runs in a Fomoxa cluster registers with the directory through `DirectoryPod`, in the `Fomoxa.Rpc.Directory` assembly of this package. That assembly carries the directory's models, codecs and stubs, so the game's own `fomoxac` run does not see them.

```csharp
using Fomoxa.Rpc.Directory;

var pod = new DirectoryPod(networkManager.ServerManager, networkManager.TimeManager, new DirectoryPodSettings
{
    DirectoryAddress = "directory.cluster.local",
    Key = File.ReadAllBytes("/run/secrets/directory/pod"),
    Address = "203.0.113.7",
    Port = 7777,
    Target = "pod-7",
    Version = Application.version,
    Region = "ap-southeast",
    MaxPlayers = 64,
});
pod.OnRegistered += registration =>
{
    encryptedTransport.Tokens = new ConnectTokens(FomoxaRegistry.Default, registration.TicketKey);
    encryptedTransport.Target = "pod-7";
    networkManager.ServerManager.StartConnection(7777);
};
pod.Start();
```

`DirectoryPod` ticks on the network tick of the `TimeManager` and connects to the directory without blocking the frame. A console server passes its `NetworkRuntime` instead of the two managers.

Registration gives the pod its id and the key that opens the tickets the directory issues for it. Start the encrypted listener after the first registration. If the directory gives the pod a new id after a reconnect (`registration.ChangedPodId`), the key changes too: stop the server, set the new `ConnectTokens`, and start it again.

The pod registers again after every reconnect and sends the rooms it holds. It reports its load on the interval the directory sets: the player count, the room count, and `players / MaxPlayers`, or the value of `pod.Load` if you set it.

Rooms:

- `await pod.AcquireRoom(roomKey)` records a room the directory accepted. It fails with `UNAVAILABLE` while the pod is not registered and with the directory's status when the room belongs to another pod or the pod is draining. A failed call records nothing, so call it again after `OnRegistered`.
- `pod.ReleaseRoom(roomKey)` forgets the room at once and tells the directory when the pod is registered.

Draining:

- `OnDrain` fires with the deadline when an operator drains the pod or when the pod asks to be drained with `RequestDrain`.
- Move each room with `pod.Relocate(roomKey, tickets, data)`, then call `ServerManager.Transfer` for each player with the address, port and one of the tickets it returns.
- At the deadline the pod stops the server and fires `OnDrainDeadline`. Exiting the process is up to the game or the platform.

The package does not catch the stop signal. A .NET console server can hold off SIGTERM and drain:

```csharp
using var stopping = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    _ = pod.RequestDrain();
});
```

For a Unity player, have the platform tell the game before it stops the process, for example with a Kubernetes `preStop` hook that sends the game a request, and call `RequestDrain` there. Set the grace period of the platform longer than the drain deadline of the directory.

### Behind a gateway

When clients reach the pods through `fomoxa-gateway`, `Address` and `Port` stay the pod's own address inside the cluster: the gateway learns it from the directory and forwards datagrams there. Set `public_address` in the directory's settings to the gateway's public address, so `Allocate` and `Relocate` hand clients the gateway instead of the pod. Turn on Behind Gateway on the pod's encrypted transport before starting the server:

```csharp
encryptedTransport.BehindGateway = true;
```

With this flag the pod only accepts datagrams that carry the header the gateway adds. The encrypted UDP page of the documentation describes the mode.

When the gateway also takes WebSocket clients, set `public_websocket` in the directory's settings to the URL clients connect to; `Allocate` and `Relocate` then return it in `WebSocketUrl`. A client keeps its transport when it moves, so when you transfer a player, check `ServerManager.ArrivedOverWebSocket(peerId)` and pass `WebSocketUrl` with port 0 to a WebSocket client, or `Address` and `Port` to a UDP client.

## Source

The fRPC code in this package is copied from the fRPC repository at a pinned commit. Change it there, not here.
