# The shader compiler cache the build compiles through. CI passes the Linux cache it restores as this named build
# context (docker build --build-context shader-cache=<directory>), which replaces this empty stage; without one, every
# shader compiles.
FROM scratch AS shader-cache

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
# The same archive and checksum .github/actions/setup-dxc installs on Linux: the cache keys every entry by the DXC
# binaries' content, so a different DXC here would miss every entry CI restores.
ADD https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2609/linux_dxc_2026_09_28.x86_x64.tar.gz /tmp/dxc.tar.gz
RUN echo '96faadc7f5c282d2ffda49804beb4c3ee38127bc252b723234e3c5cdf7aa39a1  /tmp/dxc.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/dxc.tar.gz -C /usr/local --strip-components=1 \
    && dxc --version
COPY . .
ENV CI=true
# Names the per-user Puck directory outright, so the build's default shader cache is the mount below.
ENV XDG_DATA_HOME=/root/.local/share
# The cache is mounted read-write at the build's default cache directory: what the build compiles beside the restored
# entries is discarded with this step and reaches no layer.
RUN --mount=type=bind,from=shader-cache,target=/root/.local/share/Puck/shaders,rw \
    dotnet restore src/Puck.World.Silo --locked-mode \
    && dotnet publish src/Puck.World.Silo -c Release --no-restore -o /app \
    && dotnet publish src/Puck.Cli -c Release -o /puck-cli \
    && dotnet /puck-cli/Puck.Cli.dll world prepare src/Puck.World/Assets/worlds --output /app/worlds \
    && dotnet publish src/Puck.World.Azure -c Release -o /app/worlds/extensions/Puck.World.Azure \
    && dotnet publish src/Puck.HumbleGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.HumbleGamingBrick.Forge \
    && dotnet publish src/Puck.AdvancedGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.AdvancedGamingBrick.Forge \
    && dotnet publish src/Puck.Mcp -c Release --no-restore -o /app/worlds/extensions/Puck.Mcp \
    && dotnet publish src/Puck.Mcp.Azure -c Release -o /app/worlds/extensions/Puck.Mcp.Azure

# The official caddy image, through the Docker Official Images mirror on ECR Public rather than Docker Hub, whose
# anonymous pulls rate-limit and time out on shared CI runners; the index digest pins the exact build.
FROM public.ecr.aws/docker/library/caddy:2.11.6@sha256:3422ce6de165df66534f9b9ba50efaf457114ec961763cc52f5dbdaac2972d73 AS tls
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
