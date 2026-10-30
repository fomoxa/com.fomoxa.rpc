# Fomoxa RPC for Unity

`com.fomoxa.rpc` brings fRPC, the RPC layer of the Fomoxa system, to Unity. A Unity client calls the game's backend through it, and a Unity game server (a pod) calls the directory. It runs on the transports of `com.fomoxa.networking`, so TLS over TCP, `wss://` and the browser's WebSocket in WebGL all work. Projects that do not need fRPC do not install it; `com.fomoxa.networking` does not depend on it.

| | |
|---|---|
| Version | 0.1.0 |
| Needs | `com.fomoxa.networking` 0.1.0 |
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

## Source

The fRPC code in this package is copied from the fRPC repository at a pinned commit. Change it there, not here.
