using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 本地输出桥的控制面客户端：一次请求一条 NUL 结尾的命令，读回完整响应；
/// 设备数据流则保持长连接。线协议细节由 OutputBridgeWire 负责。
/// </summary>
public sealed class OutputBridgeClient
{
    private const int ReceiveChunkBytes = 4096;

    private readonly string host;
    private readonly int port;
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan deviceCreateTimeout = TimeSpan.FromSeconds(20);

    public OutputBridgeClient(string host, int port)
    {
        this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        this.port = port;
    }

    public string Host => host;

    public int Port => port;

    public Task<string> PingAsync(CancellationToken cancellationToken) =>
        RequestAsync("ping", null, cancellationToken);

    public async Task<IReadOnlyList<uint>> BusListAsync(CancellationToken cancellationToken) =>
        OutputBridgeWire.ParseBusIds(await RequestAsync("bus/list", null, cancellationToken));

    public async Task<IReadOnlyList<BridgeDeviceInfo>> BusDevicesAsync(
        uint busId,
        CancellationToken cancellationToken) =>
        OutputBridgeWire.ParseDevices(
            await RequestAsync("bus/" + busId + "/list", null, cancellationToken), busId);

    public async Task<uint> BusCreateAsync(CancellationToken cancellationToken) =>
        OutputBridgeWire.ParseCreatedBusId(
            await RequestAsync("bus/create", "0", cancellationToken));

    public Task RemoveBusAsync(uint busId, CancellationToken cancellationToken) =>
        RequestAsync("bus/remove", busId.ToString(), cancellationToken);

    public async Task<BridgeDeviceInfo> AddDeviceAsync(
        uint busId,
        string deviceType,
        IReadOnlyDictionary<string, object?>? deviceSpecific,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?> { ["type"] = deviceType };
        if (deviceSpecific is { Count: > 0 })
        {
            request["deviceSpecific"] = deviceSpecific;
        }

        string response = await RequestAsync(
            "bus/" + busId + "/add",
            JsonSerializer.Serialize(request),
            cancellationToken,
            deviceCreateTimeout);
        return OutputBridgeWire.ParseAddedDevice(response, deviceType);
    }

    public Task RemoveDeviceAsync(uint busId, string devId, CancellationToken cancellationToken) =>
        RequestAsync("bus/" + busId + "/remove", devId, cancellationToken);

    public async Task<BridgeDeviceStream> OpenStreamAsync(
        uint busId,
        string devId,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        var tcp = CreateTcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token);
            NetworkStream stream = tcp.GetStream();
            byte[] request = Encoding.UTF8.GetBytes(OutputBridgeWire.BuildStreamPath(busId, devId));
            await stream.WriteAsync(request, linked.Token);
            return new BridgeDeviceStream(tcp, stream);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException(
                "Output bridge stream timed out: " + host + ":" + port +
                " bus=" + busId + " dev=" + devId);
        }
    }

    private async Task<string> RequestAsync(
        string path,
        string? payload,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(requestTimeout ?? timeout);

        using var tcp = CreateTcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, linked.Token);
            await using NetworkStream stream = tcp.GetStream();

            byte[] request = Encoding.UTF8.GetBytes(OutputBridgeWire.BuildRequestLine(path, payload));
            await stream.WriteAsync(request, linked.Token);

            using var accumulated = new MemoryStream();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(
                        buffer.AsMemory(0, ReceiveChunkBytes), linked.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    accumulated.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // GetBuffer 避免 ToArray 的整包复制；解码后再统一裁掉 NUL/换行填充。
            string response = OutputBridgeWire.DecodeReply(
                accumulated.GetBuffer().AsSpan(0, (int)accumulated.Length));
            OutputBridgeWire.ThrowIfApiError(response);
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Output bridge request timed out: " + host + ":" + port + " path=" + path);
        }
    }

    private static TcpClient CreateTcpClient()
    {
        var tcp = new TcpClient
        {
            NoDelay = true,
            SendBufferSize = 64 * 1024,
            ReceiveBufferSize = 64 * 1024
        };
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        return tcp;
    }
}

/// <summary>长连接数据流。热路径直接写调用方缓冲的一段，避免逐帧复制。</summary>
public sealed class BridgeDeviceStream : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;

    public BridgeDeviceStream(TcpClient tcp, NetworkStream stream)
    {
        this.tcp = tcp;
        this.stream = stream;
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken) =>
        stream.WriteAsync(data, cancellationToken).AsTask();

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        stream.WriteAsync(data, cancellationToken).AsTask();

    public async Task<byte[]> ReadExactAsync(int size, CancellationToken cancellationToken)
    {
        var data = new byte[size];
        int offset = 0;
        while (offset < data.Length)
        {
            int read = await stream.ReadAsync(
                data.AsMemory(offset, data.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Output bridge device stream closed.");
            }

            offset += read;
        }

        return data;
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
