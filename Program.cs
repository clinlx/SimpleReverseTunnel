using System;
using System.Threading.Tasks;

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

            string mode = args[0].ToLower();

            try
            {
                if (mode == "server")
                {
                    // server <bridge_port> <public_port> <password> [protocol]
                    if (args.Length < 4) throw new ArgumentException("服务端参数不足 (至少4个)");
                    if (args.Length > 5) throw new ArgumentException("服务端参数过多 (最多5个)");

                    if (!int.TryParse(args[1], out int bridgePort)) throw new ArgumentException($"无效的桥接端口: {args[1]}");
                    if (!int.TryParse(args[2], out int publicPort)) throw new ArgumentException($"无效的公共端口: {args[2]}");
                    string password = args[3];
                    
                    string? protocolStr = args.Length > 4 ? args[4] : null;
                    var protocol = ParseProtocol(protocolStr);

                    var server = new RpaServer(bridgePort, publicPort, password, protocol);
                    await server.RunAsync();
                }
                else if (mode == "client")
                {
                    // client <server_ip> <server_port> <target_ip> <target_port> <password> [protocol]
                    if (args.Length < 6) throw new ArgumentException("客户端参数不足 (至少6个)");
                    if (args.Length > 7) throw new ArgumentException("客户端参数过多 (最多7个)");

                    string serverIp = args[1];
                    if (!int.TryParse(args[2], out int serverPort)) throw new ArgumentException($"无效的服务器端口: {args[2]}");
                    string targetIp = args[3];
                    if (!int.TryParse(args[4], out int targetPort)) throw new ArgumentException($"无效的目标端口: {args[4]}");
                    string password = args[5];

                    string? protocolStr = args.Length > 6 ? args[6] : null;
                    var protocol = ParseProtocol(protocolStr);

                    var client = new RpaClient(serverIp, serverPort, targetIp, targetPort, password, protocol);
                    await client.RunAsync();
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

        static System.Net.Sockets.ProtocolType ParseProtocol(string? protocolStr)
        {
            if (string.IsNullOrEmpty(protocolStr)) return System.Net.Sockets.ProtocolType.Tcp;
            
            var normalized = protocolStr.ToLower();
            if (normalized == "tcp") return System.Net.Sockets.ProtocolType.Tcp;
            if (normalized == "udp") return System.Net.Sockets.ProtocolType.Udp;
            
            throw new ArgumentException($"不支持的协议参数: '{protocolStr}'。仅支持 'tcp' 或 'udp' (留空默认为 tcp)。");
        }

        static void ShowUsage()
        {
            Console.WriteLine("SimpleReverseTunnel - 安全高效的内网穿透工具 (TCP/UDP)");
            Console.WriteLine("用法:");
            Console.WriteLine("  服务端: SimpleReverseTunnel.exe server <bridge_port> <public_port> <password> [tcp|udp]");
            Console.WriteLine("  客户端: SimpleReverseTunnel.exe client <server_ip> <bridge_port> <target_ip> <target_port> <password> [tcp|udp]");
            Console.WriteLine("示例:");
            Console.WriteLine("  服务端: SimpleReverseTunnel.exe server 9000 9001 MySecretPass udp");
            Console.WriteLine("  客户端: SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 53 MySecretPass udp");
        }
    }
}
