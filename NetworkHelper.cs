using System;
using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public static class NetworkHelper
    {
        public const int BufferSize = 8192;
        public static readonly byte[] MagicBytes = { 0x4E, 0x52, 0x50, 0x41 }; // NRPA

        public enum ConnectionType : byte
        {
            Control = 0x00,
            Data = 0x01
        }

        public static async Task SendHandshakeAsync(SecureSocket socket, ConnectionType type, Guid? connectionId = null)
        {
            // 协议: [Magic(4)] [Type(1)] [Payload(16, optional)]
            
            int len = 4 + 1 + (type == ConnectionType.Data ? 16 : 0);
            var buffer = ArrayPool<byte>.Shared.Rent(len);
            
            try
            {
                var span = buffer.AsSpan(0, len);
                int offset = 0;

                // Magic
                MagicBytes.CopyTo(span.Slice(offset, 4));
                offset += 4;

                // Type
                span[offset] = (byte)type;
                offset += 1;

                // Payload
                if (type == ConnectionType.Data && connectionId.HasValue)
                {
                    connectionId.Value.TryWriteBytes(span.Slice(offset, 16));
                    offset += 16;
                }

                await socket.SendAsync(buffer.AsMemory(0, len));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Returns: (IsSuccess, ConnectionType, ConnectionId)
        public static async Task<(bool, ConnectionType, Guid)> ReceiveHandshakeAsync(SecureSocket socket)
        {
            // Read Magic (4)
            byte[] header = new byte[4];
            if (!await ReadExactAsync(socket, header))
            {
                Logger.Warn($"读取协议头失败: {socket.RemoteEndPoint}");
                return (false, default, default);
            }

            if (!header.AsSpan().SequenceEqual(MagicBytes))
            {
                Logger.Warn($"协议校验失败: {socket.RemoteEndPoint}");
                return (false, default, default);
            }

            // Read Type
            byte[] typeBuf = new byte[1];
            if (!await ReadExactAsync(socket, typeBuf))
            {
                Logger.Warn($"读取类型失败: {socket.RemoteEndPoint}");
                return (false, default, default);
            }
            ConnectionType type = (ConnectionType)typeBuf[0];

            Guid connId = Guid.Empty;
            if (type == ConnectionType.Data)
            {
                byte[] idBuf = new byte[16];
                if (!await ReadExactAsync(socket, idBuf))
                {
                    Logger.Warn($"读取连接ID失败: {socket.RemoteEndPoint}");
                    return (false, default, default);
                }
                connId = new Guid(idBuf);
            }

            return (true, type, connId);
        }

        public static async Task<bool> ReadExactAsync(SecureSocket socket, Memory<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await socket.ReceiveAsync(buffer.Slice(totalRead));
                if (read == 0) return false;
                totalRead += read;
            }
            return true;
        }

        // 桥接逻辑: 
        // 1. 用户 (明文) <-> 桥接 (密文)
        // 2. 控制 (密文)
        
        // 控制连接直接在 RpaServer/Client 中使用 SecureSocket
        // 数据转发需要处理 明文 <-> 密文 转换

        public static async Task ForwardAsync(Socket plainSide, SecureSocket secureSide)
        {
            try
            {
                var t1 = TransferAsync(plainSide, secureSide);
                var t2 = TransferAsync(secureSide, plainSide);
                await Task.WhenAll(t1, t2);
            }
            catch { /* Ignore forwarding errors */ }
            finally
            {
                CleanupSocket(plainSide);
                secureSide.Dispose(); // SecureSocket.Dispose calls InnerSocket.Dispose
            }
        }

        private static async Task TransferAsync(Socket source, SecureSocket destination)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    int read = await source.ReceiveAsync(buffer, SocketFlags.None);
                    if (read == 0) break;
                    await destination.SendAsync(buffer.AsMemory(0, read));
                }
            }
            catch 
            {
                // Connection lost
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try { destination.Shutdown(SocketShutdown.Send); } catch {}
            }
        }

        private static async Task TransferAsync(SecureSocket source, Socket destination)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    int read = await source.ReceiveAsync(buffer);
                    if (read == 0) break;
                    await destination.SendAsync(buffer.AsMemory(0, read), SocketFlags.None);
                }
            }
            catch 
            {
                // Connection lost
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try { destination.Shutdown(SocketShutdown.Send); } catch {}
            }
        }

        public static void CleanupSocket(Socket socket)
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch {}
            try { socket.Close(); } catch {}
            try { socket.Dispose(); } catch {}
        }

        public static async Task ForwardUdpAsync(Socket udpSide, SecureSocket secureSide)
        {
            try
            {
                var t1 = TransferUdpToTcpAsync(udpSide, secureSide);
                var t2 = TransferTcpToUdpAsync(secureSide, udpSide);
                await Task.WhenAll(t1, t2);
            }
            catch { /* Ignore */ }
            finally
            {
                CleanupSocket(udpSide);
                secureSide.Dispose();
            }
        }

        private static async Task TransferUdpToTcpAsync(Socket udpSource, SecureSocket tcpDest)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65535);
            byte[] lenBuf = new byte[2]; // Reuse allocation
            try
            {
                while (true)
                {
                    // UDP receive
                    int read = await udpSource.ReceiveAsync(buffer, SocketFlags.None);
                    if (read == 0) break; 

                    // Write Length (Big Endian)
                    lenBuf[0] = (byte)(read >> 8);
                    lenBuf[1] = (byte)(read);
                    
                    await tcpDest.SendAsync(lenBuf);
                    await tcpDest.SendAsync(buffer.AsMemory(0, read));
                }
            }
            catch
            {
                // Error
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task TransferTcpToUdpAsync(SecureSocket tcpSource, Socket udpDest)
        {
            byte[] lenBuf = new byte[2]; // Reuse allocation
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65535);
            try
            {
                while (true)
                {
                    // Read Length
                    if (!await ReadExactAsync(tcpSource, lenBuf)) break;
                    
                    int length = (lenBuf[0] << 8) | lenBuf[1];
                    if (length > buffer.Length) 
                    {
                        break; 
                    }

                    // Read Payload
                    if (!await ReadExactAsync(tcpSource, buffer.AsMemory(0, length))) break;

                    // Send UDP
                    await udpDest.SendAsync(buffer.AsMemory(0, length), SocketFlags.None);
                }
            }
            catch
            {
                // Error
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
