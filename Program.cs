using System.Net.Sockets;

namespace SimpleReverseTunnel
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            string mode = args[0].ToLowerInvariant();

            try
            {
                if (mode == "server")
                {
                    await RunServerAsync(args);
                }
                else if (mode == "client")
                {
                    await RunClientAsync(args);
                }
                else
                {
                    throw new ArgumentException($"未知模式: '{mode}'，仅支持 'server' 或 'client'");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"错误: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                ShowUsage();
            }
        }

        private static async Task RunServerAsync(string[] args)
        {
            if (args.Length == 2)
            {
                if (!int.TryParse(args[1], out int bridgePort)) throw new ArgumentException($"无效的桥接端口: {args[1]}");

                string? mapValue = Environment.GetEnvironmentVariable(TunnelMapping.EnvironmentVariableName);
                IReadOnlyList<TunnelMapping> mappings = TunnelMapping.ParseMany(mapValue ?? string.Empty);
                var server = new RpaServer(bridgePort, mappings);
                await server.RunAsync();
                return;
            }

            if (args.Length < 4) throw new ArgumentException("服务端参数不足。新模式使用 server <bridge_port>，旧模式使用 server <bridge_port> <public_port> <password> [tcp|udp]");
            if (args.Length > 5) throw new ArgumentException("服务端参数过多");

            if (!int.TryParse(args[1], out int legacyBridgePort)) throw new ArgumentException($"无效的桥接端口: {args[1]}");
            if (!int.TryParse(args[2], out int publicPort)) throw new ArgumentException($"无效的公共端口: {args[2]}");

            string password = args[3];
            ProtocolType protocol = ParseProtocol(args.Length > 4 ? args[4] : null);

            var legacyServer = new RpaServer(legacyBridgePort, publicPort, password, protocol);
            await legacyServer.RunAsync();
        }

        private static async Task RunClientAsync(string[] args)
        {
            if (args.Length < 6) throw new ArgumentException("客户端参数不足");
            if (args.Length > 7) throw new ArgumentException("客户端参数过多");

            string serverIp = args[1];
            if (!int.TryParse(args[2], out int serverPort)) throw new ArgumentException($"无效的服务器端口: {args[2]}");
            string targetIp = args[3];
            if (!int.TryParse(args[4], out int targetPort)) throw new ArgumentException($"无效的目标端口: {args[4]}");
            string password = args[5];
            ProtocolType protocol = ParseProtocol(args.Length > 6 ? args[6] : null);

            var client = new RpaClient(serverIp, serverPort, targetIp, targetPort, password, protocol);
            await client.RunAsync();
        }

        static ProtocolType ParseProtocol(string? protocolStr)
        {
            if (string.IsNullOrEmpty(protocolStr)) return ProtocolType.Tcp;

            string normalized = protocolStr.ToLowerInvariant();
            if (normalized == "tcp") return ProtocolType.Tcp;
            if (normalized == "udp") return ProtocolType.Udp;

            throw new ArgumentException($"不支持的协议参数: '{protocolStr}'。仅支持 'tcp' 或 'udp'。");
        }

        static void ShowUsage()
        {
            Console.WriteLine("SimpleReverseTunnel - 内网穿透工具 (TCP/UDP)");
            Console.WriteLine("用法:");
            Console.WriteLine("  服务端(新): 先设置 REVERSE_TUNNEL_MAP，再运行 SimpleReverseTunnel.exe server <bridge_port>");
            Console.WriteLine("  服务端(旧): SimpleReverseTunnel.exe server <bridge_port> <public_port> <password> [tcp|udp]");
            Console.WriteLine("  客户端:     SimpleReverseTunnel.exe client <server_ip> <bridge_port> <target_ip> <target_port> <password> [tcp|udp]");
            Console.WriteLine("示例:");
            Console.WriteLine("  set REVERSE_TUNNEL_MAP=8080:MySecret1:udp,8081:MySecret2:udp,8082:MySecret3:udp");
            Console.WriteLine("  SimpleReverseTunnel.exe server 2560");
            Console.WriteLine("  SimpleReverseTunnel.exe client 1.2.3.4 2560 127.0.0.1 53 MySecret1 udp");
            Console.WriteLine("  SimpleReverseTunnel.exe server 2560 8080 MySecret1 udp");
        }
    }
}
