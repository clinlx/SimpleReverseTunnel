namespace SimpleReverseTunnel
{
    public sealed record TunnelMapping(int PublicPort, string Password, TunnelProtocol Protocol)
    {
        public const string EnvironmentVariableName = "REVERSE_TUNNEL_MAP";

        public static IReadOnlyList<TunnelMapping> ParseMany(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{EnvironmentVariableName} 不能为空");
            }

            var mappings = new List<TunnelMapping>();
            var passwords = new HashSet<string>(StringComparer.Ordinal);

            foreach (string rawEntry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = rawEntry.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 3)
                {
                    throw new ArgumentException($"无效的 {EnvironmentVariableName} 配置项: {rawEntry}");
                }

                if (!int.TryParse(parts[0], out int publicPort) || publicPort <= 0 || publicPort > 65535)
                {
                    throw new ArgumentException($"无效的公共端口: {parts[0]}");
                }

                string password = parts[1];
                if (string.IsNullOrEmpty(password))
                {
                    throw new ArgumentException("Secret 不能为空");
                }

                if (!passwords.Add(password))
                {
                    throw new ArgumentException("REVERSE_TUNNEL_MAP 中的 Secret 不可重复");
                }

                mappings.Add(new TunnelMapping(publicPort, password, ParseProtocol(parts[2])));
            }

            if (mappings.Count == 0)
            {
                throw new ArgumentException($"{EnvironmentVariableName} 至少需要一个映射");
            }

            return mappings;
        }

        private static TunnelProtocol ParseProtocol(string value)
        {
            string normalized = value.ToLowerInvariant();
            return normalized switch
            {
                "tcp" => TunnelProtocol.Tcp,
                "udp" => TunnelProtocol.Udp,
                "all" => TunnelProtocol.All,
                _ => throw new ArgumentException($"不支持的协议参数: '{value}'。仅支持 'tcp'、'udp' 或 'all'。")
            };
        }
    }
}
