using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public class RpaServer
    {
        private readonly int _bridgePort;
        private readonly int _publicPort;
        private readonly string _password;
        private readonly System.Net.Sockets.ProtocolType _protocol;

        private TcpListener _bridgeListener;
        private TcpListener? _publicListener;
        private Socket? _publicUdpSocket;
        
        // 控制连接：同一时间只允许一个活动代理
        private SecureSocket? _controlSocket;
        private readonly object _controlLock = new();

        // 挂起的连接：ID -> Socket (来自桥接的数据连接)
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<SecureSocket>> _pendingConnections = new();

        // UDP 会话管理
        private class UdpSession
        {
            public SecureSocket? BridgeSocket;
            public DateTime LastActive;
            public readonly object Lock = new();
            public TaskCompletionSource<SecureSocket> ConnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly ConcurrentDictionary<EndPoint, UdpSession> _udpSessions = new();

        public RpaServer(int bridgePort, int publicPort, string password, System.Net.Sockets.ProtocolType protocol = System.Net.Sockets.ProtocolType.Tcp)
        {
            _bridgePort = bridgePort;
            _publicPort = publicPort;
            _password = password;
            _protocol = protocol;

            _bridgeListener = new TcpListener(IPAddress.Any, _bridgePort);
            if (_protocol == System.Net.Sockets.ProtocolType.Tcp)
            {
                _publicListener = new TcpListener(IPAddress.Any, _publicPort);
            }
        }

        public async Task RunAsync()
        {
            Logger.Info("服务端启动...");
            Logger.Info($"模式: {_protocol}");
            Logger.Info($"桥接端口: {_bridgePort} (TCP)");
            Logger.Info($"公共端口: {_publicPort} ({_protocol})");
            // Logger.Info($"Password: {_password}"); // 不要记录密码

            _bridgeListener.Start();
            
            Task publicTask;
            if (_protocol == System.Net.Sockets.ProtocolType.Tcp)
            {
                _publicListener!.Start();
                publicTask = AcceptPublicConnectionsAsync();
            }
            else
            {
                publicTask = AcceptPublicUdpAsync();
            }

            var bridgeTask = AcceptBridgeConnectionsAsync();
            
            Logger.Info("服务已就绪，等待连接...");
            await Task.WhenAll(bridgeTask, publicTask);
            Logger.Info("服务意外停止");
        }

        private async Task AcceptBridgeConnectionsAsync()
        {
            Logger.Info("监听桥接连接...");
            while (true)
            {
                try
                {
                    var socket = await _bridgeListener.AcceptSocketAsync();
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
            var socket = new SecureSocket(rawSocket, _password);
            try
            {
                using var cts = new CancellationTokenSource(5000);

                var (success, type, connId) = await NetworkHelper.ReceiveHandshakeAsync(socket);
                
                if (!success)
                {
                    Logger.Warn($"认证失败: {socket.RemoteEndPoint}");
                    socket.Dispose();
                    return;
                }
                
                Logger.Info($"握手成功: {type} {connId}");

                if (type == NetworkHelper.ConnectionType.Control)
                {
                    RegisterControlSocket(socket);
                    _ = SendHeartbeatsAsync(socket);
                    await MonitorControlConnectionAsync(socket);
                }
                else if (type == NetworkHelper.ConnectionType.Data)
                {
                    if (_pendingConnections.TryGetValue(connId, out var tcs))
                    {
                        tcs.TrySetResult(socket);
                    }
                    else
                    {
                        Logger.Warn($"无效数据连接ID: {connId}");
                        socket.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"握手异常: {ex.Message}");
                socket.Dispose();
            }
        }

        private async Task SendHeartbeatsAsync(SecureSocket socket)
        {
            try
            {
                byte[] heartbeat = new byte[17]; // 命令(1) + 填充(16)
                heartbeat[0] = 0x00;
                
                while (socket.Connected)
                {
                    await Task.Delay(5000);
                    await SendControlCommandAsync(socket, heartbeat);
                }
            }
            catch
            {
                // 心跳失败，可能已断开连接
            }
        }

        private async Task MonitorControlConnectionAsync(SecureSocket socket)
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
                    else
                    {
                         Logger.Warn($"控制连接收到异常数据 ({read} bytes)，已忽略");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"控制连接异常: {ex.Message}");
            }
            finally
            {
                CleanupControlSocket(socket);
            }
        }

        private void CleanupControlSocket(SecureSocket socket)
        {
            lock (_controlLock)
            {
                if (_controlSocket == socket)
                {
                    _controlSocket = null;
                    Logger.Info("控制连接已清理");
                }
            }
            socket.Dispose();
        }

        private void RegisterControlSocket(SecureSocket socket)
        {
            lock (_controlLock)
            {
                if (_controlSocket != null)
                {
                    Logger.Info("替换旧的控制连接");
                    _controlSocket.Dispose();
                }
                _controlSocket = socket;
                Logger.Info($"控制连接已注册: {socket.RemoteEndPoint}");
            }
        }

        private async Task AcceptPublicConnectionsAsync()
        {
            if (_publicListener == null) return;
            Logger.Info("监听公共连接...");
            while (true)
            {
                try
                {
                    var userSocket = await _publicListener.AcceptSocketAsync();
                    _ = HandleUserConnectionAsync(userSocket);
                }
                catch (Exception ex)
                {
                    Logger.Error($"公共端口监听异常: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task HandleUserConnectionAsync(Socket userSocket)
        {
            SecureSocket? control = null;
            lock (_controlLock)
            {
                if (_controlSocket != null && _controlSocket.Connected)
                {
                    control = _controlSocket;
                }
            }

            if (control == null)
            {
                // Logger.Warn("没有活动的代理连接，拒绝用户请求。");
                NetworkHelper.CleanupSocket(userSocket);
                return;
            }

            Guid connId = Guid.NewGuid();
            // Logger.Info($"新用户请求 {connId}");

            var tcs = new TaskCompletionSource<SecureSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConnections[connId] = tcs;

            try
            {
                // 发送请求给客户端
                // 协议: [命令(1)] [连接ID(16)]
                byte[] cmd = new byte[17];
                cmd[0] = 0x01; // RequestConnect
                connId.TryWriteBytes(cmd.AsSpan(1));
                
                await SendControlCommandAsync(control, cmd);

                // 等待数据连接
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                
                if (completedTask == tcs.Task)
                {
                    SecureSocket bridgeDataSocket = await tcs.Task;
                    await NetworkHelper.ForwardAsync(userSocket, bridgeDataSocket);
                }
                else
                {
                    // 超时
                    // Logger.Warn($"等待桥接数据超时 {connId}");
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
                _pendingConnections.TryRemove(connId, out _);
            }
        }

        private readonly SemaphoreSlim _controlSendLock = new(1, 1);
        private async Task SendControlCommandAsync(SecureSocket socket, byte[] data)
        {
            await _controlSendLock.WaitAsync();
            try
            {
                await socket.SendAsync(data);
            }
            finally
            {
                _controlSendLock.Release();
            }
        }

        private async Task AcceptPublicUdpAsync()
        {
            Logger.Info("监听公共连接 (UDP)...");
            _publicUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            _publicUdpSocket.Bind(new IPEndPoint(IPAddress.Any, _publicPort));
            
            // 启动清理任务
            _ = CleanupUdpSessionsAsync();

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535);
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (true)
                {
                    try
                    {
                        var result = await _publicUdpSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEP);
                        // result.RemoteEndPoint is the source
                        
                        int len = result.ReceivedBytes;
                        byte[] packetData = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
                        Array.Copy(buffer, packetData, len);
                        
                        _ = HandleUdpPacketAsync(packetData, len, result.RemoteEndPoint);
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

        private async Task CleanupUdpSessionsAsync()
        {
            while (true)
            {
                await Task.Delay(30000);
                var now = DateTime.UtcNow;
                foreach (var kvp in _udpSessions)
                {
                    if ((now - kvp.Value.LastActive).TotalSeconds > 60)
                    {
                        if (_udpSessions.TryRemove(kvp.Key, out var session))
                        {
                            try { session.BridgeSocket?.Dispose(); } catch {}
                        }
                    }
                }
            }
        }

        private async Task HandleUdpPacketAsync(byte[] data, int len, EndPoint remoteEP)
        {
            try
            {
                // Logger.Info($"收到UDP数据包: {len} bytes 来自 {remoteEP}"); // 高频日志已注释
                UdpSession? session;
                bool isNew = false;
                
                if (!_udpSessions.TryGetValue(remoteEP, out session))
                {
                    session = new UdpSession { LastActive = DateTime.UtcNow };
                    if (_udpSessions.TryAdd(remoteEP, session))
                    {
                        isNew = true;
                    }
                    else
                    {
                        _udpSessions.TryGetValue(remoteEP, out session);
                    }
                }
                
                if (session == null) return;

                session.LastActive = DateTime.UtcNow;

                if (isNew)
                {
                    _ = InitializeUdpSessionAsync(session, remoteEP);
                }

                try
                {
                    SecureSocket bridgeSocket = await session.ConnectTcs.Task;
                    
                    // 合并头和数据以减少系统调用和开销
                    int totalLen = len + 2;
                    byte[] sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLen);
                    try
                    {
                        sendBuffer[0] = (byte)(len >> 8);
                        sendBuffer[1] = (byte)(len);
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
                    _udpSessions.TryRemove(remoteEP, out _);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(data);
            }
        }

        private async Task InitializeUdpSessionAsync(UdpSession session, EndPoint remoteEP)
        {
            SecureSocket? control = null;
            lock (_controlLock)
            {
                if (_controlSocket != null && _controlSocket.Connected)
                {
                    control = _controlSocket;
                }
            }

            if (control == null)
            {
                session.ConnectTcs.TrySetException(new Exception("No control connection"));
                return;
            }

            Guid connId = Guid.NewGuid();
            var tcs = new TaskCompletionSource<SecureSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingConnections[connId] = tcs;

            try
            {
                byte[] cmd = new byte[17];
                cmd[0] = 0x01; // RequestConnect
                connId.TryWriteBytes(cmd.AsSpan(1));
                
                await SendControlCommandAsync(control, cmd);

                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                if (completedTask == tcs.Task)
                {
                    session.BridgeSocket = await tcs.Task;
                    session.ConnectTcs.TrySetResult(session.BridgeSocket);
                    
                    // Start reading from bridge to send back to UDP
                    _ = ProcessBridgeToUdpAsync(session, remoteEP);
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
                _pendingConnections.TryRemove(connId, out _);
            }
        }

        private async Task ProcessBridgeToUdpAsync(UdpSession session, EndPoint remoteEP)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535);
            try
            {
                byte[] lenBuf = new byte[2];
                var socket = session.BridgeSocket!;

                while (true)
                {
                    if (!await NetworkHelper.ReadExactAsync(socket, lenBuf)) break;
                    int len = (lenBuf[0] << 8) | lenBuf[1];
                    
                    if (len > buffer.Length) break;

                    if (!await NetworkHelper.ReadExactAsync(socket, buffer.AsMemory(0, len))) break;

                    // Logger.Info($"转发UDP响应: {len} bytes 到 {remoteEP}");
                    await _publicUdpSocket!.SendToAsync(buffer.AsMemory(0, len), SocketFlags.None, remoteEP);
                    session.LastActive = DateTime.UtcNow;
                }
            }
            catch
            {
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                _udpSessions.TryRemove(remoteEP, out _);
                try { session.BridgeSocket?.Dispose(); } catch {}
            }
        }
    }
}
