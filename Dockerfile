# Use the .NET SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install dependencies for AOT publishing
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy project files and restore dependencies
COPY ["SimpleReverseTunnel.csproj", "./"]
RUN dotnet restore "SimpleReverseTunnel.csproj"

# Copy the rest of the source code
COPY . .

# Publish the application
RUN dotnet publish "SimpleReverseTunnel.csproj" -c Release -r linux-x64 -o /app/publish

# Use ubuntu image for the final stage
FROM ubuntu:22.04 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Shared mode selector: server or client
ENV MODE=server

# Server configuration
ENV BRIDGE_PORT=2560
ENV REVERSE_TUNNEL_MAP=8080:MySecret:tcp

# Client configuration
ENV SERVER_IP=127.0.0.1
ENV SERVER_PORT=2560
ENV TARGET_IP=127.0.0.1
ENV TARGET_PORT=80
ENV PASSWORD=MySecret
ENV PROTOCOL=tcp

ENTRYPOINT ["/bin/sh", "-c", "if [ \"$MODE\" = \"server\" ]; then exec ./SimpleReverseTunnel server $BRIDGE_PORT; elif [ \"$MODE\" = \"client\" ]; then exec ./SimpleReverseTunnel client $SERVER_IP $SERVER_PORT $TARGET_IP $TARGET_PORT $PASSWORD $PROTOCOL; else echo \"Unsupported MODE: $MODE (expected server or client)\" >&2; exit 1; fi"]
