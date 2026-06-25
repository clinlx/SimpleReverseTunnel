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

        public SecureSocket(Socket socket, string password, int initialReceiveIndex = 0)
            : this(socket, DeriveKey(password), initialReceiveIndex)
        {
        }

        public SecureSocket(Socket socket, byte[] key, int initialReceiveIndex = 0)
        {
            InnerSocket = socket;
            _key = key;
            _recvIndex = initialReceiveIndex;
        }

        public static byte[] DeriveKey(string password)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        }

        public static void ApplyXor(string password, Span<byte> data, int keyOffset = 0)
        {
            byte[] key = DeriveKey(password);
            ApplyXor(data, key, keyOffset);
        }

        public static void ApplyXor(byte[] key, ReadOnlySpan<byte> source, Span<byte> destination, int keyOffset = 0)
        {
            source.CopyTo(destination);
            ApplyXor(destination, key, keyOffset);
        }

        public bool Connected => InnerSocket.Connected;
        public EndPoint? RemoteEndPoint => InnerSocket.RemoteEndPoint;

        public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await InnerSocket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (read > 0)
            {
                ApplyXor(buffer.Span.Slice(0, read), _key, ref _recvIndex);
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
                ApplyXor(temp.AsSpan(0, buffer.Length), _key, ref _sendIndex);
                await InnerSocket.SendAsync(temp.AsMemory(0, buffer.Length), SocketFlags.None);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyXor(Span<byte> data, byte[] key, ref int keyIndex)
        {
            ApplyXor(data, key, keyIndex);
            keyIndex = (keyIndex + data.Length) % int.MaxValue;
        }

        private static void ApplyXor(Span<byte> data, byte[] key, int keyOffset)
        {
            int i = 0;
            int len = data.Length;
            int kLen = key.Length;

            // 避免循环中取模
            int kIdx = keyOffset % kLen;
            
            for (; i < len; i++)
            {
                data[i] ^= key[kIdx];
                kIdx++;
                if (kIdx == kLen) kIdx = 0;
            }
        }

        public void Shutdown(SocketShutdown how) => InnerSocket.Shutdown(how);
        public void Close() => InnerSocket.Close();
        public void Dispose() => InnerSocket.Dispose();
    }
}
