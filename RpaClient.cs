using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public class RpaClient
    {
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly string _targetIp;
        private readonly int _targetPort;
        private readonly string _password;
        private readonly System.Net.Sockets.ProtocolType _protocol;

        public RpaClient(string serverIp, int serverPort, string targetIp, int targetPort, string password, System.Net.Sockets.ProtocolType protocol = System.Net.Sockets.ProtocolType.Tcp)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
            _targetIp = targetIp;
            _targetPort = targetPort;
            _password = password;
            _protocol = protocol;
        }

        public async Task RunAsync()
        {
            Logger.Info($"目标: {_targetIp}:{_targetPort} ({_protocol})");
            Logger.Info($"服务器: {_serverIp}:{_serverPort}");

            while (true)
            {
                try
                {
                    await MaintainControlConnectionAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error($"控制连接异常: {ex.Message}");
                }
                
                Logger.Info("3秒后重连...");
                await Task.Delay(3000);
            }
        }

        private async Task MaintainControlConnectionAsync()
        {
            Logger.Info("正在连接服务器...");
            using Socket rawSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await rawSocket.ConnectAsync(_serverIp, _serverPort);
            using SecureSocket controlSocket = new SecureSocket(rawSocket, _password);
            
            Logger.Info("已连接");

            // 握手
            await NetworkHelper.SendHandshakeAsync(controlSocket, NetworkHelper.ConnectionType.Control);

            // 读取指令循环
            byte[] buffer = new byte[17]; // 1 命令 + 16 ID
            while (true)
            {
                if (!await NetworkHelper.ReadExactAsync(controlSocket, buffer))
                {
                    throw new Exception("服务器关闭了连接");
                }

                byte cmd = buffer[0];
                if (cmd == 0x00) // Heartbeat
                {
                    // Ignore
                }
                else if (cmd == 0x01) // RequestConnect
                {
                    Guid connId = new Guid(buffer.AsSpan(1, 16));
                    _ = HandleProxyRequestAsync(connId);
                }
                else
                {
                    Logger.Warn($"未知指令: {cmd}");
                }
            }
        }

        private async Task HandleProxyRequestAsync(Guid connId)
        {
            SecureSocket? serverDataSocket = null;
            Socket? targetSocket = null;

            try
            {
                // 1. 连接到服务器 (桥接端口)
                Socket rawSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await rawSocket.ConnectAsync(_serverIp, _serverPort);
                serverDataSocket = new SecureSocket(rawSocket, _password);

                // 2. 握手 (数据模式)
                await NetworkHelper.SendHandshakeAsync(serverDataSocket, NetworkHelper.ConnectionType.Data, connId);

                // 3. 连接到目标服务
                if (_protocol == System.Net.Sockets.ProtocolType.Tcp)
                {
                    targetSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await targetSocket.ConnectAsync(_targetIp, _targetPort);

                    // 4. 开始转发
                    await NetworkHelper.ForwardAsync(targetSocket, serverDataSocket);
                }
                else
                {
                    targetSocket = new Socket(SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                    await targetSocket.ConnectAsync(_targetIp, _targetPort);

                    // 4. 开始转发 (UDP)
                    await NetworkHelper.ForwardUdpAsync(targetSocket, serverDataSocket);
                }
            }
            catch (Exception)
            {
                if (serverDataSocket != null) serverDataSocket.Dispose();
                if (targetSocket != null) NetworkHelper.CleanupSocket(targetSocket);
            }
        }
    }
}
