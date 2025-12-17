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
# 格式: SimpleReverseTunnel server <bridge_port> <public_port> <password> [protocol]
# protocol 可选: tcp 或 udp

# 示例 1: 启动 TCP 代理 (默认)
dotnet run -- server 9000 9001 MySecret
# 对于编译后的文件 ，改为执行：
SimpleReverseTunnel.exe server 9000 9001 MySecret

# 示例 2: 启动 UDP 代理
dotnet run -- server 9000 9001 MySecret udp
# 对于编译后的文件 ，改为执行：
SimpleReverseTunnel.exe server 9000 9001 MySecret udp
```

### 2. 客户端 (内网机器)

启动客户端，连接服务端并将流量转发到本地目标服务。

```bash
# 格式: SimpleReverseTunnel client <server_ip> <bridge_port> <target_ip> <target_port> <password> [protocol]
# protocol 可选: tcp 或 udp，必须与服务端保持一致

# 示例 1: 转发本地 TCP 服务 (默认)
dotnet run -- client 1.2.3.4 9000 127.0.0.1 80 MySecret
# 对于编译后的文件 ，改为执行：
SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 80 MySecret

# 示例 2: 转发本地 UDP 服务
dotnet run -- client 1.2.3.4 9000 127.0.0.1 53 MySecret udp
# 对于编译后的文件 ，改为执行：
SimpleReverseTunnel.exe client 1.2.3.4 9000 127.0.0.1 53 MySecret udp
```

### 3. 访问

访问公网机器的 Public Port (如9001)，流量将被转发到内网机器的 Target Port (如80) 上。

## Docker 部署

本项目支持 Docker 部署，并提供了基于 `ubuntu:22.04` 的 Native AOT 镜像。

### 1. 服务端

使用 host 网络模式以获得最佳性能。

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-server \
  -e BRIDGE_PORT=9000 \
  -e PUBLIC_PORT=9001 \
  -e PASSWORD=MySecret \
  swr.cn-south-1.myhuaweicloud.com/tunnel/simple-reverse-tunnel-server:latest
```

### 2. 客户端

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-client \
  -e SERVER_IP=1.2.3.4 \
  -e SERVER_PORT=9000 \
  -e TARGET_IP=127.0.0.1 \
  -e TARGET_PORT=80 \
  -e PASSWORD=MySecret \
  swr.cn-south-1.myhuaweicloud.com/tunnel/simple-reverse-tunnel-client:latest
```

> 注意：使用 `--network host` 模式时，客户端配置中的 `TARGET_IP=127.0.0.1` 将直接指向宿主机（Host Machine），方便转发宿主机上的服务（如 Nginx、数据库等）。

### 3. 构建镜像

如果你想自己构建镜像，可以参考以下步骤：

#### 3.1 构建服务端镜像

```bash
docker build -f Dockerfile.server -t simple-reverse-tunnel-server .
```

#### 3.2 构建客户端镜像

```bash
docker build -f Dockerfile.client -t simple-reverse-tunnel-client .
```

#### 3.3 运行自构建镜像

构建完成后，将上述 `docker run` 命令中的镜像名称替换为你刚刚构建的名称（如 `simple-reverse-tunnel-server`）即可。

> **注意**: 默认提供的镜像仓库地址 (`swr.cn-south-1.myhuaweicloud.com/tunnel/...`) 仅供示例或特定部署使用。如果你 fork 了本项目，建议修改 `.github/workflows/docker-build.yml` 中的仓库配置，并配置你自己的 Secrets。