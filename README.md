# SimpleReverseTunnel

## 编译

首先确保安装了 .NET SDK 10.0 或更高版本。

微软官方文档：[安装 .NET SDK](https://learn.microsoft.com/zh-cn/dotnet/core/install/)

执行编译命令：

```bash
dotnet build -c Release
```

## 使用说明

### 1. 服务端（公网机器）

服务端监听一个 Bridge Port 供客户端连接，并根据 `REVERSE_TUNNEL_MAP` 启动多个外部访问端口。

- **Bridge Port**：供内网客户端连接，多个客户端共用同一个端口。
- **Public Port**：供外部用户访问，协议类型由映射配置指定。
- **Secret**：客户端连接时使用的密码，服务端根据 Secret 自动绑定到对应 Public Port。

> 注意：`tcp` / `udp` / `all` 表示被穿透的业务端口协议；服务端和客户端之间的 Bridge Port 通信始终基于 TCP。
> 注意：`REVERSE_TUNNEL_MAP` 中的 Secret 不可重复，否则服务端无法判断客户端应绑定到哪个 Public Port。
> 注意：`all` 表示同一个 Public Port 同时监听 TCP 和 UDP；新版客户端使用 `all` 时会在一个进程内同时转发 TCP 和 UDP。

#### 新用法（推荐）

```bash
# 格式: REVERSE_TUNNEL_MAP=<public_port>:<secret>:<tcp|udp|all>[,<public_port>:<secret>:<tcp|udp|all>...]
# 格式: SimpleReverseTunnel server <bridge_port>
```

Windows PowerShell：

```powershell
$env:REVERSE_TUNNEL_MAP="8080:WebSecret:tcp,5353:DnsSecret:udp,9001:GameSecret:all"
dotnet run -- server 2560

# 对于编译后的文件，改为执行：
SimpleReverseTunnel.exe server 2560
```

Windows CMD：

```bat
set REVERSE_TUNNEL_MAP=8080:WebSecret:tcp,5353:DnsSecret:udp,9001:GameSecret:all
SimpleReverseTunnel.exe server 2560
```

Linux：

```bash
export REVERSE_TUNNEL_MAP='8080:WebSecret:tcp,5353:DnsSecret:udp,9001:GameSecret:all'
dotnet run -- server 2560

# 对于编译后的文件，改为执行：
./SimpleReverseTunnel server 2560
```

#### 兼容旧用法

```bash
# 格式: SimpleReverseTunnel server <bridge_port> <public_port> <password> [protocol]
# protocol 可选: tcp、udp 或 all，表示被穿透的业务端口协议
```

Windows：

```powershell
SimpleReverseTunnel.exe server 2560 8080 WebSecret tcp
SimpleReverseTunnel.exe server 2560 5353 DnsSecret udp
SimpleReverseTunnel.exe server 2560 9001 GameSecret all
```

Linux：

```bash
./SimpleReverseTunnel server 2560 8080 WebSecret tcp
./SimpleReverseTunnel server 2560 5353 DnsSecret udp
./SimpleReverseTunnel server 2560 9001 GameSecret all
```

### 2. 客户端（内网机器）

客户端命令保持不变。客户端使用的 Secret 决定它绑定到服务端的哪个 Public Port。

```bash
# 格式: SimpleReverseTunnel client <server_ip> <bridge_port> <target_ip> <target_port> <password> [protocol]
# protocol 可选: tcp、udp 或 all，表示被穿透的目标业务协议，必须与服务端映射中的协议保持一致
```

Windows：

```powershell
# WebSecret 绑定到服务端 8080/tcp，并转发到内网 127.0.0.1:80
SimpleReverseTunnel.exe client 1.2.3.4 2560 127.0.0.1 80 WebSecret tcp

# DnsSecret 绑定到服务端 5353/udp，并转发到内网 127.0.0.1:53
SimpleReverseTunnel.exe client 1.2.3.4 2560 127.0.0.1 53 DnsSecret udp

# GameSecret 绑定到服务端 9001/all，并同时转发内网 127.0.0.1:9001 的 TCP 和 UDP
SimpleReverseTunnel.exe client 1.2.3.4 2560 127.0.0.1 9001 GameSecret all
```

Linux：

```bash
# WebSecret 绑定到服务端 8080/tcp，并转发到内网 127.0.0.1:80
./SimpleReverseTunnel client 1.2.3.4 2560 127.0.0.1 80 WebSecret tcp

# DnsSecret 绑定到服务端 5353/udp，并转发到内网 127.0.0.1:53
./SimpleReverseTunnel client 1.2.3.4 2560 127.0.0.1 53 DnsSecret udp

# GameSecret 绑定到服务端 9001/all，并同时转发内网 127.0.0.1:9001 的 TCP 和 UDP
./SimpleReverseTunnel client 1.2.3.4 2560 127.0.0.1 9001 GameSecret all
```

### 3. 访问

用户访问公网机器的 Public Port（如 8080），流量将被转发到使用对应 Secret 连接的内网客户端 Target Port（如 80）上。

服务本身的 Bridge Port（如 2560）仅用于客户端和服务端之间的 TCP 连接通信，不用于外部业务访问。

## Docker 部署

本项目支持 Docker 部署，并提供基于 `ubuntu:22.04` 的 Native AOT 镜像。

统一镜像通过 `MODE` 环境变量决定启动服务端或客户端：

- `MODE=server`：启动服务端。
- `MODE=client`：启动客户端。

以下示例使用镜像：`registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest`。

### 1. 服务端

使用 host 网络模式时，容器直接监听宿主机端口：

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-server \
  -e MODE=server \
  -e BRIDGE_PORT=2560 \
  -e REVERSE_TUNNEL_MAP=8080:WebSecret:tcp,5353:DnsSecret:udp,9001:GameSecret:all \
  registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest
```

不使用 host 网络模式时，需要显式映射 Bridge Port 和每个 Public Port。TCP 端口使用普通 `-p`，UDP 端口需要加 `/udp`：

```bash
docker run -d \
  --restart unless-stopped \
  --name tunnel-server \
  -p 2560:2560/tcp \
  -p 8080:8080/tcp \
  -p 5353:5353/udp \
  -p 9001:9001/udp \
  -e MODE=server \
  -e BRIDGE_PORT=2560 \
  -e REVERSE_TUNNEL_MAP=8080:WebSecret:tcp,5353:DnsSecret:udp,9001:GameSecret:all \
  registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest
```

### 2. 客户端

客户端容器需要主动连接公网服务端。若目标服务运行在同一个宿主机上，推荐使用 host 网络模式，让 `TARGET_IP=127.0.0.1` 直接指向宿主机。

TCP Web 示例：

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-client-web \
  -e MODE=client \
  -e SERVER_IP=1.2.3.4 \
  -e SERVER_PORT=2560 \
  -e TARGET_IP=127.0.0.1 \
  -e TARGET_PORT=80 \
  -e PASSWORD=WebSecret \
  -e PROTOCOL=tcp \
  registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest
```

UDP DNS 示例：

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-client-dns \
  -e MODE=client \
  -e SERVER_IP=1.2.3.4 \
  -e SERVER_PORT=2560 \
  -e TARGET_IP=127.0.0.1 \
  -e TARGET_PORT=53 \
  -e PASSWORD=DnsSecret \
  -e PROTOCOL=udp \
  registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest
```

TCP + UDP 同端口示例：

```bash
docker run -d \
  --network host \
  --restart unless-stopped \
  --name tunnel-client-game \
  -e MODE=client \
  -e SERVER_IP=1.2.3.4 \
  -e SERVER_PORT=2560 \
  -e TARGET_IP=127.0.0.1 \
  -e TARGET_PORT=9001 \
  -e PASSWORD=GameSecret \
  -e PROTOCOL=all \
  registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest
```

如果客户端容器不使用 host 网络模式，容器内的 `127.0.0.1` 只代表容器本身，不能直接访问宿主机服务。此时应将 `TARGET_IP` 设置为宿主机在容器可达网络中的 IP，或使用 Docker 提供的宿主机地址（例如 Docker Desktop 的 `host.docker.internal`）。

### 3. 构建镜像

如果想自行构建镜像，可以参考以下步骤：

#### 3.1 构建统一镜像

```bash
docker build -f Dockerfile -t simple-reverse-tunnel .
```

#### 3.2 运行自构建镜像

构建完成后，将上面 `docker run` 命令中的镜像地址替换为 `simple-reverse-tunnel:latest` 即可。

> **注意**：默认示例使用 `registry.cn-hangzhou.aliyuncs.com/algorithm_space/simple_reverse_tunnel:latest`，自行构建时可替换为自己的镜像名称。
