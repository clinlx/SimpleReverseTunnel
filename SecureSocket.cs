using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public class SecureSocket : IDisposable
    {
        public Socket InnerSocket { get; }
        private readonly byte[] _key;
        private int _sendIndex;
        private int _recvIndex;

        public SecureSocket(Socket socket, string password)
        {
            InnerSocket = socket;
            // SHA256 确保密钥分布均匀
            using var sha256 = SHA256.Create();
            _key = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        }

        public bool Connected => InnerSocket.Connected;
        public EndPoint? RemoteEndPoint => InnerSocket.RemoteEndPoint;

        public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await InnerSocket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read > 0)
            {
                ApplyXor(buffer.Span.Slice(0, read), ref _recvIndex);
            }
            return read;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> buffer)
        {
            // 租用缓冲区避免修改原始数据 (XOR 是原地的)
            byte[] temp = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(temp);
                ApplyXor(temp.AsSpan(0, buffer.Length), ref _sendIndex);
                await InnerSocket.SendAsync(temp.AsMemory(0, buffer.Length), SocketFlags.None);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyXor(Span<byte> data, ref int keyIndex)
        {
            int i = 0;
            int len = data.Length;
            int kLen = _key.Length;

            // 避免循环中取模
            int kIdx = keyIndex % kLen;
            
            for (; i < len; i++)
            {
                data[i] ^= _key[kIdx];
                kIdx++;
                if (kIdx == kLen) kIdx = 0;
            }
            
            keyIndex = (keyIndex + len) % int.MaxValue;
        }

        public void Shutdown(SocketShutdown how) => InnerSocket.Shutdown(how);
        public void Close() => InnerSocket.Close();
        public void Dispose() => InnerSocket.Dispose();
    }
}
