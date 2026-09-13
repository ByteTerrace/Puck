FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
ADD https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz /tmp/dxc.tar.gz
RUN echo '55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54  /tmp/dxc.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/dxc.tar.gz -C /usr/local --strip-components=1 \
    && dxc --version
COPY . .
ENV CI=true
RUN dotnet restore src/Puck.World.Silo --locked-mode \
    && dotnet publish src/Puck.World.Silo -c Release --no-restore -o /app \
    && dotnet publish src/Puck.Cli -c Release -o /puck-cli \
    && dotnet /puck-cli/Puck.Cli.dll world prepare src/Puck.World/Assets/worlds /app/worlds \
    && dotnet publish src/Puck.World.Azure -c Release --no-restore -o /app/worlds/extensions/Puck.World.Azure \
    && dotnet publish src/Puck.HumbleGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.HumbleGamingBrick.Forge \
    && dotnet publish src/Puck.AdvancedGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.AdvancedGamingBrick.Forge \
    && dotnet publish src/Puck.Mcp -c Release --no-restore -o /app/worlds/extensions/Puck.Mcp

FROM caddy:2.11.4 AS tls
# TLS binds 8443; the upstream low-port file capability prevents execution with --cap-drop ALL.
RUN setcap -r /usr/bin/caddy

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
COPY --from=tls /usr/bin/caddy /usr/bin/caddy
ADD https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb /tmp/packages-microsoft-prod.deb
RUN echo 'c13f01ac7c3001b51a9281d40dde666db5e037e05512840c319832f7852bfec4  /tmp/packages-microsoft-prod.deb' | sha256sum -c - \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends libmsquic \
    && rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb
WORKDIR /app
# Keep the authored asset layout beside the composed worlds. Machine content paths are relative to that world
# directory; the exact image digest retains the cartridges and other dependencies with the runtime.
COPY --from=build /source/src/Puck.World/Assets .
COPY --from=build /app .
# Each extension is published with its dependency manifest into a matching directory, so the loader discovers
# only its entry assembly. Both entry points scan ./extensions from /app/worlds; the silo stays provider-neutral.
COPY --from=build /puck-cli /puck-cli
# The deployment supplies its silo document and federation keys as mounted files.
WORKDIR /app/worlds
ENTRYPOINT ["dotnet", "/app/Puck.World.Silo.dll"]
CMD ["--silo", "/configuration/silo.json"]
