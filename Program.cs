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
                    if (args.Length < 4) throw new ArgumentException("Missing arguments for server mode.");
                    int bridgePort = int.Parse(args[1]);
                    int publicPort = int.Parse(args[2]);
                    string password = args[3];
                    
                    System.Net.Sockets.ProtocolType protocol = System.Net.Sockets.ProtocolType.Tcp;
                    if (args.Length > 4 && args[4].ToLower() == "udp")
                    {
                        protocol = System.Net.Sockets.ProtocolType.Udp;
                    }

                    var server = new RpaServer(bridgePort, publicPort, password, protocol);
                    await server.RunAsync();
                }
                else if (mode == "client")
                {
                    // client <server_ip> <server_port> <target_ip> <target_port> <password> [protocol]
                    if (args.Length < 6) throw new ArgumentException("Missing arguments for client mode.");
                    string serverIp = args[1];
                    int serverPort = int.Parse(args[2]);
                    string targetIp = args[3];
                    int targetPort = int.Parse(args[4]);
                    string password = args[5];

                    System.Net.Sockets.ProtocolType protocol = System.Net.Sockets.ProtocolType.Tcp;
                    if (args.Length > 6 && args[6].ToLower() == "udp")
                    {
                        protocol = System.Net.Sockets.ProtocolType.Udp;
                    }

                    var client = new RpaClient(serverIp, serverPort, targetIp, targetPort, password, protocol);
                    await client.RunAsync();
                }
                else
                {
                    ShowUsage();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack: {ex.StackTrace}");
            }
        }

        static void ShowUsage()
        {
            Console.WriteLine("SimpleReverseTunnel - Secure & Fast Tunnel (TCP/UDP)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  Server: SimpleReverseTunnel.exe server <bridge_port> <public_port> <password> [tcp|udp]");
            Console.WriteLine("  Client: SimpleReverseTunnel.exe client <server_ip> <bridge_port> <target_ip> <target_port> <password> [tcp|udp]");
            Console.WriteLine("Example:");
            Console.WriteLine("  Server: SimpleReverseTunnel.exe server 9000 9001 MySecretPass udp");
            Console.WriteLine("  Client: SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 53 MySecretPass udp");
        }
    }
}
