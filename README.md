# SimpleReverseTunnel

## 编译

确保安装了 .NET SDK 8.0 或更高版本。

```bash
dotnet build -c Release
```

## 使用说明

### 1. 服务端 (公网机器)

启动服务端，监听两个端口：
- **Bridge Port**: 供内网客户端连接 (TCP隧道)
- **Public Port**: 供外部用户访问 (协议类型根据参数指定)

```bash
# 格式: SimpleReverseTunnel.exe server <bridge_port> <public_port> <password> [protocol]
# protocol 可选: tcp 或 udp

# 示例 1: 启动 TCP 代理 (默认)
# dotnet run -- server 9000 9001 MySecret
SimpleReverseTunnel.exe server 9000 9001 MySecret

# 示例 2: 启动 UDP 代理
# dotnet run -- server 9000 9001 MySecret udp
SimpleReverseTunnel.exe server 9000 9001 MySecret udp
```

### 2. 客户端 (内网机器)

启动客户端，连接服务端并将流量转发到本地目标服务。

```bash
# 格式: SimpleReverseTunnel.exe client <server_ip> <bridge_port> <target_ip> <target_port> <password> [protocol]
# protocol 可选: tcp 或 udp，必须与服务端保持一致

# 示例 1: 转发本地 TCP 服务 (默认)
# dotnet run -- client 1.2.3.4 9000 127.0.0.1 80 MySecret
SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 80 MySecret

# 示例 2: 转发本地 UDP 服务
# dotnet run -- client 1.2.3.4 9000 127.0.0.1 53 MySecret udp
SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 53 MySecret udp
```

### 3. 访问

访问公网机器的 Public Port (如9001)，流量将被转发到内网机器的 Target Port (如80) 上。