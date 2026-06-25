using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SimpleReverseTunnel
{
    public class RpaServer
    {
        private const int ControlCommandLength = 17;
        private const int ControlHandshakeLength = 5;
        private const int DataHandshakeLength = 21;

        private readonly int _bridgePort;
        private readonly IReadOnlyList<TunnelContext> _contexts;
        private readonly TcpListener _bridgeListener;

        private sealed class TunnelContext
        {
            public TunnelContext(TunnelMapping mapping)
            {
                Mapping = mapping;
                Key = SecureSocket.DeriveKey(mapping.Password);
                if (mapping.Protocol is TunnelProtocol.Tcp or TunnelProtocol.All)
                {
                    PublicListener = new TcpListener(IPAddress.Any, mapping.PublicPort);
                }
            }

            public TunnelMapping Mapping { get; }
            public byte[] Key { get; }
            public TcpListener? PublicListener { get; }
            public Socket? PublicUdpSocket;
            public SecureSocket? TcpControlSocket;
            public SecureSocket? UdpControlSocket;
            public SecureSocket? LegacyControlSocket;
            public object ControlLock { get; } = new();
            public SemaphoreSlim ControlSendLock { get; } = new(1, 1);
            public ConcurrentDictionary<Guid, TaskCompletionSource<SecureSocket>> PendingConnections { get; } = new();
            public ConcurrentDictionary<EndPoint, UdpSession> UdpSessions { get; } = new();
        }

        private sealed class UdpSession
        {
            public SecureSocket? BridgeSocket;
            public DateTime LastActive;
            public TaskCompletionSource<SecureSocket> ConnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public RpaServer(int bridgePort, int publicPort, string password, TunnelProtocol protocol = TunnelProtocol.Tcp)
            : this(bridgePort, new[] { new TunnelMapping(publicPort, password, protocol) })
        {
        }

        public RpaServer(int bridgePort, IReadOnlyList<TunnelMapping> mappings)
        {
            if (mappings.Count == 0)
            {
                throw new ArgumentException("至少需要一个端口映射");
            }

            _bridgePort = bridgePort;
            _contexts = mappings.Select(mapping => new TunnelContext(mapping)).ToArray();
            _bridgeListener = new TcpListener(IPAddress.Any, _bridgePort);
        }

        public async Task RunAsync()
        {
            Logger.Info("服务端启动...");
            Logger.Info($"桥接端口: {_bridgePort} (Tcp)");
            foreach (TunnelContext context in _contexts)
            {
                Logger.Info($"公共端口: {context.Mapping.PublicPort} ({context.Mapping.Protocol})");
            }

            _bridgeListener.Start();

            var tasks = new List<Task> { AcceptBridgeConnectionsAsync() };
            foreach (TunnelContext context in _contexts)
            {
                if (context.Mapping.Protocol is TunnelProtocol.Tcp or TunnelProtocol.All)
                {
                    context.PublicListener!.Start();
                    tasks.Add(AcceptPublicConnectionsAsync(context));
                }

                if (context.Mapping.Protocol is TunnelProtocol.Udp or TunnelProtocol.All)
                {
                    tasks.Add(AcceptPublicUdpAsync(context));
                }
            }

            Logger.Info("服务已就绪，等待连接...");
            await Task.WhenAll(tasks);
            Logger.Info("服务意外停止");
        }

        private async Task AcceptBridgeConnectionsAsync()
        {
            Logger.Info("监听桥接连接...");
            while (true)
            {
                try
                {
                    Socket socket = await _bridgeListener.AcceptSocketAsync();
                    _ = HandleBridgeHandshakeAsync(socket);
                }
                catch (Exception ex)
                {
                    Logger.Error($"桥接监听异常: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleBridgeHandshakeAsync(Socket rawSocket)
        {
            try
            {
                using var cts = new CancellationTokenSource(5000);
                byte[] controlHandshake = new byte[ControlHandshakeLength];
                if (!await ReadRawExactAsync(rawSocket, controlHandshake, cts.Token))
                {
                    NetworkHelper.CleanupSocket(rawSocket);
                    return;
                }

                HandshakeMatch? match = TryMatchHandshake(controlHandshake, null);
                if (match == null && TryGetConnectionType(controlHandshake, out NetworkHelper.ConnectionType type) && type == NetworkHelper.ConnectionType.Data)
                {
                    byte[] dataHandshake = new byte[DataHandshakeLength];
                    controlHandshake.CopyTo(dataHandshake, 0);
                    if (!await ReadRawExactAsync(rawSocket, dataHandshake.AsMemory(ControlHandshakeLength), cts.Token))
                    {
                        NetworkHelper.CleanupSocket(rawSocket);
                        return;
                    }

                    match = TryMatchHandshake(dataHandshake, NetworkHelper.ConnectionType.Data);
                }

                if (match == null)
                {
                    Logger.Warn($"认证失败: {rawSocket.RemoteEndPoint}");
                    NetworkHelper.CleanupSocket(rawSocket);
                    return;
                }

                var secureSocket = new SecureSocket(rawSocket, match.Context.Key, match.ConsumedBytes);
                Logger.Info(FormatHandshakeSuccessMessage(match.Type, match.ConnectionId, match.Context.Mapping, rawSocket.RemoteEndPoint));

                if (match.Type == NetworkHelper.ConnectionType.Control)
                {
                    ProtocolType? clientProtocol = await ReadClientProtocolAsync(match.Context, secureSocket);
                    if (RegisterControlConnection(match.Context, secureSocket, clientProtocol))
                    {
                        _ = SendHeartbeatsAsync(match.Context, secureSocket);
                        await MonitorControlConnectionAsync(match.Context, secureSocket);
                    }
                }
                else if (match.Context.PendingConnections.TryGetValue(match.ConnectionId, out var tcs))
                {
                    tcs.TrySetResult(secureSocket);
                }
                else
                {
                    Logger.Warn($"无效数据连接ID: {match.ConnectionId}");
                    secureSocket.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"握手异常: {ex.Message}");
                NetworkHelper.CleanupSocket(rawSocket);
            }
        }

        private async Task<ProtocolType?> ReadClientProtocolAsync(TunnelContext context, SecureSocket socket)
        {
            byte[] buffer = new byte[1];
            using var cts = new CancellationTokenSource(250);
            try
            {
                int read = await socket.ReceiveAsync(buffer, cts.Token);
                if (read == 1)
                {
                    ProtocolType? protocol = buffer[0] switch
                    {
                        0x02 => ProtocolType.Tcp,
                        0x03 => ProtocolType.Udp,
                        _ => null
                    };

                    if (protocol.HasValue)
                    {
                        return protocol;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }

            if (context.Mapping.Protocol != TunnelProtocol.All)
            {
                return context.Mapping.Protocol == TunnelProtocol.Udp ? ProtocolType.Udp : ProtocolType.Tcp;
            }

            return null;
        }

        private bool RegisterControlConnection(TunnelContext context, SecureSocket socket, ProtocolType? clientProtocol)
        {
            lock (context.ControlLock)
            {
                ref SecureSocket? controlSocket = ref GetControlSocketSlot(context, clientProtocol);

                if (!IsControlSocketUsable(controlSocket))
                {
                    ClearControlSocketUnderLock(context, controlSocket);
                }

                if (controlSocket != null)
                {
                    Logger.Warn($"拒绝新的控制连接 {socket.RemoteEndPoint}: 已有活动连接 {controlSocket.RemoteEndPoint}");
                    socket.Dispose();
                    return false;
                }

                controlSocket = socket;
                Logger.Info($"控制连接已注册: {socket.RemoteEndPoint}");
                return true;
            }
        }

        private async Task SendHeartbeatsAsync(TunnelContext context, SecureSocket socket)
        {
            try
            {
                byte[] heartbeat = new byte[ControlCommandLength];
                heartbeat[0] = 0x00;

                while (socket.Connected)
                {
                    await Task.Delay(5000);
                    await SendControlCommandAsync(context, socket, heartbeat);
                }
            }
            catch
            {
            }
            finally
            {
                CleanupControlSocket(context, socket);
            }
        }

        private async Task MonitorControlConnectionAsync(TunnelContext context, SecureSocket socket)
        {
            try
            {
                byte[] buffer = new byte[1024];
                while (true)
                {
                    int read = await socket.ReceiveAsync(buffer);
                    if (read == 0)
                    {
                        Logger.Info("客户端控制连接已断开");
                        break;
                    }

                    Logger.Warn($"控制连接收到异常数据 ({read} bytes)，已忽略");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"控制连接异常: {ex.Message}");
            }
            finally
            {
                CleanupControlSocket(context, socket);
            }
        }

        private void CleanupControlSocket(TunnelContext context, SecureSocket socket)
        {
            lock (context.ControlLock)
            {
                if (context.TcpControlSocket == socket || context.UdpControlSocket == socket || context.LegacyControlSocket == socket)
                {
                    ClearControlSocketUnderLock(context, socket);
                }
            }
            socket.Dispose();
        }

        private SecureSocket? GetActiveControlSocket(TunnelContext context, ProtocolType requestedProtocol)
        {
            lock (context.ControlLock)
            {
                ref SecureSocket? controlSocket = ref GetControlSocketSlot(context, requestedProtocol);
                if (!IsControlSocketUsable(controlSocket))
                {
                    ClearControlSocketUnderLock(context, controlSocket);
                }

                if (controlSocket != null)
                {
                    return controlSocket;
                }

                if (!IsControlSocketUsable(context.LegacyControlSocket))
                {
                    ClearControlSocketUnderLock(context, context.LegacyControlSocket);
                }

                return context.LegacyControlSocket;
            }
        }

        private static ref SecureSocket? GetControlSocketSlot(TunnelContext context, ProtocolType? protocol)
        {
            if (protocol == ProtocolType.Tcp)
            {
                return ref context.TcpControlSocket;
            }

            if (protocol == ProtocolType.Udp)
            {
                return ref context.UdpControlSocket;
            }

            return ref context.LegacyControlSocket;
        }

        private static bool IsControlSocketUsable(SecureSocket? socket)
        {
            if (socket == null || !socket.Connected)
            {
                return false;
            }

            try
            {
                Socket innerSocket = socket.InnerSocket;
                return !(innerSocket.Poll(0, SelectMode.SelectRead) && innerSocket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private static void ClearControlSocketUnderLock(TunnelContext context, SecureSocket? socket)
        {
            if (socket == null)
            {
                return;
            }

            if (context.TcpControlSocket == socket)
            {
                context.TcpControlSocket = null;
                Logger.Info("控制连接已清理");
            }
            else if (context.UdpControlSocket == socket)
            {
                context.UdpControlSocket = null;
                Logger.Info("控制连接已清理");
            }
            else if (context.LegacyControlSocket == socket)
            {
                context.LegacyControlSocket = null;
                Logger.Info("控制连接已清理");
            }

            socket.Dispose();
        }

        private async Task AcceptPublicConnectionsAsync(TunnelContext context)
        {
            Logger.Info($"监听公共连接: {context.Mapping.PublicPort} (Tcp)");
            while (true)
            {
                try
                {
                    Socket userSocket = await context.PublicListener!.AcceptSocketAsync();
                    _ = HandleUserConnectionAsync(context, userSocket);
                }
                catch (Exception ex)
                {
                    Logger.Error($"公共端口监听异常: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleUserConnectionAsync(TunnelContext context, Socket userSocket)
        {
            SecureSocket? control = GetActiveControlSocket(context, ProtocolType.Tcp);
            if (control == null)
            {
                NetworkHelper.CleanupSocket(userSocket);
                return;
            }

            Guid connId = Guid.NewGuid();
            var tcs = new TaskCompletionSource<SecureSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.PendingConnections[connId] = tcs;

            try
            {
                byte[] cmd = new byte[ControlCommandLength];
                cmd[0] = 0x01;
                connId.TryWriteBytes(cmd.AsSpan(1));

                await SendControlCommandAsync(context, control, cmd);

                Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                if (completedTask == tcs.Task)
                {
                    SecureSocket bridgeDataSocket = await tcs.Task;
                    await NetworkHelper.ForwardAsync(userSocket, bridgeDataSocket);
                }
                else
                {
                    NetworkHelper.CleanupSocket(userSocket);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"用户连接处理失败 {connId}: {ex.Message}");
                NetworkHelper.CleanupSocket(userSocket);
            }
            finally
            {
                context.PendingConnections.TryRemove(connId, out _);
            }
        }

        private static async Task SendControlCommandAsync(TunnelContext context, SecureSocket socket, byte[] data)
        {
            await context.ControlSendLock.WaitAsync();
            try
            {
                await socket.SendAsync(data);
            }
            finally
            {
                context.ControlSendLock.Release();
            }
        }

        private async Task AcceptPublicUdpAsync(TunnelContext context)
        {
            Logger.Info($"监听公共连接: {context.Mapping.PublicPort} (Udp)");
            context.PublicUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            context.PublicUdpSocket.Bind(new IPEndPoint(IPAddress.Any, context.Mapping.PublicPort));
            _ = CleanupUdpSessionsAsync(context);

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535);
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (true)
                {
                    try
                    {
                        SocketReceiveFromResult result = await context.PublicUdpSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP);
                        _ = HandleUdpPacketAsync(context, result.RemoteEndPoint, buffer.AsMemory(0, result.ReceivedBytes));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"UDP 监听异常: {ex.Message}");
                        await Task.Delay(1000);
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task CleanupUdpSessionsAsync(TunnelContext context)
        {
            while (true)
            {
                await Task.Delay(30000);
                DateTime now = DateTime.UtcNow;
                foreach (var pair in context.UdpSessions.ToArray())
                {
                    if ((now - pair.Value.LastActive).TotalSeconds > 60)
                    {
                        if (context.UdpSessions.TryRemove(pair.Key, out UdpSession? session))
                        {
                            try { session.BridgeSocket?.Dispose(); } catch { }
                        }
                    }
                }
            }
        }

        private async Task HandleUdpPacketAsync(TunnelContext context, EndPoint remoteEP, ReadOnlyMemory<byte> packet)
        {
            byte[] data = System.Buffers.ArrayPool<byte>.Shared.Rent(packet.Length);
            try
            {
                packet.CopyTo(data);
                int len = packet.Length;

                bool isNew = false;
                if (!context.UdpSessions.TryGetValue(remoteEP, out UdpSession? session))
                {
                    var newSession = new UdpSession { LastActive = DateTime.UtcNow };
                    if (context.UdpSessions.TryAdd(remoteEP, newSession))
                    {
                        session = newSession;
                        isNew = true;
                    }
                    else
                    {
                        context.UdpSessions.TryGetValue(remoteEP, out session);
                    }
                }

                if (session == null)
                {
                    return;
                }

                session.LastActive = DateTime.UtcNow;
                if (isNew)
                {
                    _ = InitializeUdpSessionAsync(context, session, remoteEP);
                }

                try
                {
                    SecureSocket bridgeSocket = await session.ConnectTcs.Task;
                    int totalLen = len + 2;
                    byte[] sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLen);
                    try
                    {
                        sendBuffer[0] = (byte)(len >> 8);
                        sendBuffer[1] = (byte)len;
                        Array.Copy(data, 0, sendBuffer, 2, len);

                        await bridgeSocket.SendAsync(sendBuffer.AsMemory(0, totalLen));
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(sendBuffer);
                    }
                }
                catch
                {
                    context.UdpSessions.TryRemove(remoteEP, out _);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(data);
            }
        }

        private async Task InitializeUdpSessionAsync(TunnelContext context, UdpSession session, EndPoint remoteEP)
        {
            SecureSocket? control = GetActiveControlSocket(context, ProtocolType.Udp);
            if (control == null)
            {
                session.ConnectTcs.TrySetException(new Exception("No control connection"));
                return;
            }

            Guid connId = Guid.NewGuid();
            var tcs = new TaskCompletionSource<SecureSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.PendingConnections[connId] = tcs;

            try
            {
                byte[] cmd = new byte[ControlCommandLength];
                cmd[0] = 0x01;
                connId.TryWriteBytes(cmd.AsSpan(1));

                await SendControlCommandAsync(context, control, cmd);

                Task completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (completedTask == tcs.Task)
                {
                    session.BridgeSocket = await tcs.Task;
                    session.ConnectTcs.TrySetResult(session.BridgeSocket);
                    _ = ProcessBridgeToUdpAsync(context, session, remoteEP);
                }
                else
                {
                    session.ConnectTcs.TrySetException(new TimeoutException());
                }
            }
            catch (Exception ex)
            {
                session.ConnectTcs.TrySetException(ex);
            }
            finally
            {
                context.PendingConnections.TryRemove(connId, out _);
            }
        }

        private async Task ProcessBridgeToUdpAsync(TunnelContext context, UdpSession session, EndPoint remoteEP)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535);
            try
            {
                byte[] lenBuf = new byte[2];
                SecureSocket socket = session.BridgeSocket!;

                while (true)
                {
                    if (!await NetworkHelper.ReadExactAsync(socket, lenBuf)) break;
                    int len = (lenBuf[0] << 8) | lenBuf[1];
                    if (len > buffer.Length) break;
                    if (!await NetworkHelper.ReadExactAsync(socket, buffer.AsMemory(0, len))) break;

                    await context.PublicUdpSocket!.SendToAsync(buffer.AsMemory(0, len), SocketFlags.None, remoteEP);
                    session.LastActive = DateTime.UtcNow;
                }
            }
            catch
            {
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                context.UdpSessions.TryRemove(remoteEP, out _);
                try { session.BridgeSocket?.Dispose(); } catch { }
            }
        }

        private sealed record HandshakeMatch(TunnelContext Context, NetworkHelper.ConnectionType Type, Guid ConnectionId, int ConsumedBytes);

        private static string FormatHandshakeSuccessMessage(NetworkHelper.ConnectionType type, Guid connectionId, TunnelMapping mapping, object? remoteEndPoint)
        {
            string baseMessage = $"握手成功: {type} 端口={mapping.PublicPort} 协议={mapping.Protocol} Secret={mapping.Password}";
            if (type == NetworkHelper.ConnectionType.Data)
            {
                baseMessage += $" 连接ID={connectionId}";
            }

            return $"{baseMessage} 连接={remoteEndPoint}";
        }

        private HandshakeMatch? TryMatchHandshake(byte[] encryptedHandshake, NetworkHelper.ConnectionType? expectedType)
        {
            Span<byte> candidate = stackalloc byte[DataHandshakeLength];
            foreach (TunnelContext context in _contexts)
            {
                SecureSocket.ApplyXor(context.Key, encryptedHandshake, candidate.Slice(0, encryptedHandshake.Length));

                if (!candidate.Slice(0, NetworkHelper.MagicBytes.Length).SequenceEqual(NetworkHelper.MagicBytes))
                {
                    continue;
                }

                var type = (NetworkHelper.ConnectionType)candidate[4];
                if (expectedType.HasValue && type != expectedType.Value)
                {
                    continue;
                }

                if (type == NetworkHelper.ConnectionType.Control)
                {
                    return new HandshakeMatch(context, type, Guid.Empty, ControlHandshakeLength);
                }

                if (type == NetworkHelper.ConnectionType.Data && encryptedHandshake.Length >= DataHandshakeLength)
                {
                    return new HandshakeMatch(context, type, new Guid(candidate.Slice(5, 16)), DataHandshakeLength);
                }
            }

            return null;
        }

        private bool TryGetConnectionType(ReadOnlySpan<byte> encryptedControlHandshake, out NetworkHelper.ConnectionType type)
        {
            Span<byte> candidate = stackalloc byte[ControlHandshakeLength];
            foreach (TunnelContext context in _contexts)
            {
                SecureSocket.ApplyXor(context.Key, encryptedControlHandshake, candidate);

                if (candidate.Slice(0, NetworkHelper.MagicBytes.Length).SequenceEqual(NetworkHelper.MagicBytes))
                {
                    type = (NetworkHelper.ConnectionType)candidate[4];
                    return true;
                }
            }

            type = default;
            return false;
        }

        private static async Task<bool> ReadRawExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await socket.ReceiveAsync(buffer.Slice(totalRead), SocketFlags.None, cancellationToken);
                if (read == 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }
    }
}
