# The shader compiler cache the build compiles through. CI passes the Linux cache it restores as this named build
# context (docker build --build-context shader-cache=<directory>), which replaces this empty stage; without one, every
# shader compiles.
FROM scratch AS shader-cache

FROM rust:1.96.0 AS rust-toolchain

# Remote files the image needs, fetched with retries and checked against pinned digests. ADD makes one attempt, and
# packages.microsoft.com answers shared CI runners with HTTP 429 often enough to fail a release on one request.
# The same DXC archive and checksum .github/actions/setup-dxc installs on Linux: the shader cache keys every entry by
# the DXC binaries' content, so a different DXC here would miss every entry CI restores.
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS downloads
RUN fetch() { curl --fail --silent --show-error --location --retry 6 --retry-all-errors --retry-delay 10 --output "$2" "$1" \
        && echo "$3  $2" | sha256sum -c -; } \
    && fetch https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2609/linux_dxc_2026_09_28.x86_x64.tar.gz \
        /tmp/dxc.tar.gz 96faadc7f5c282d2ffda49804beb4c3ee38127bc252b723234e3c5cdf7aa39a1 \
    && fetch https://packages.microsoft.com/config/ubuntu/26.04/packages-microsoft-prod.deb \
        /tmp/packages-microsoft-prod.deb b2e7c6b9328e0c6a68bfe5d0045be90ceb192b32e0ca906c6c529b9012726f39

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS build
WORKDIR /source
COPY --from=rust-toolchain /usr/local/cargo /usr/local/cargo
COPY --from=rust-toolchain /usr/local/rustup /usr/local/rustup
ENV CARGO_HOME=/usr/local/cargo RUSTUP_HOME=/usr/local/rustup
ENV PATH="/usr/local/cargo/bin:/usr/lib/llvm-19/bin:${PATH}"
# clang and ld.lld for world prepare's AGB BIOS come from this distribution's own LLVM 19 packages (19.1.7 on Ubuntu
# 26.04), which machine setup's 19.1.6 floor admits. LLVM's Linux release tarball cannot serve here: its lld needs
# libxml2.so.2, and Ubuntu 26.04 ships only libxml2.so.16.
RUN apt-get update \
    && apt-get install -y --no-install-recommends clang-19 lld-19 \
    && rm -rf /var/lib/apt/lists/* \
    && clang --version && ld.lld --version \
    && rustup target add --toolchain 1.96.0 wasm32-unknown-unknown
COPY --from=downloads /tmp/dxc.tar.gz /tmp/dxc.tar.gz
RUN tar -xzf /tmp/dxc.tar.gz -C /usr/local --strip-components=1 \
    && rm /tmp/dxc.tar.gz \
    && dxc --version
COPY . .
ENV CI=true
# Names the per-user Puck directory outright, so the build's default shader cache is the mount below.
ENV XDG_DATA_HOME=/root/.local/share
# The cache is mounted read-write at the build's default cache directory: what the build compiles beside the restored
# entries is discarded with this step and reaches no layer.
RUN --mount=type=bind,from=shader-cache,target=/root/.local/share/Puck/shaders,rw \
    dotnet restore src/Puck.World.Silo --locked-mode -nodeReuse:false \
    && dotnet publish src/Puck.World.Silo -c Release --no-restore -o /app -nodeReuse:false \
    && dotnet publish src/Puck.Cli -c Release -o /puck-cli -nodeReuse:false \
    && dotnet /puck-cli/Puck.Cli.dll world prepare worlds/debug --output /app/worlds \
    && dotnet publish src/Puck.World.Azure -c Release -o /app/worlds/extensions/Puck.World.Azure -nodeReuse:false \
    && dotnet publish src/Puck.HumbleGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.HumbleGamingBrick.Forge -nodeReuse:false \
    && dotnet publish src/Puck.AdvancedGamingBrick.Forge -c Release --no-restore -o /app/worlds/extensions/Puck.AdvancedGamingBrick.Forge -nodeReuse:false \
    && dotnet publish src/Puck.HumbleGamingDeck.Forge -c Release -o /app/worlds/extensions/Puck.HumbleGamingDeck.Forge -nodeReuse:false \
    && dotnet publish src/Puck.Mcp -c Release --no-restore -o /app/worlds/extensions/Puck.Mcp -nodeReuse:false \
    && dotnet publish src/Puck.Mcp.Azure -c Release -o /app/worlds/extensions/Puck.Mcp.Azure -nodeReuse:false

# The official Caddy image through Google's Docker Hub mirror: ECR Public's anonymous data limit also rejects
# shared CI runners with HTTP 429. The index digest pins the exact build, including when the tag is rebuilt.
FROM mirror.gcr.io/library/caddy:2.11.7@sha256:504a44d7d74bd2bc28a66afc4774912fc21223ca8a67734af1c54ab2f999b489 AS tls
# TLS binds 8443; the upstream low-port file capability prevents execution with --cap-drop ALL.
RUN setcap -r /usr/bin/caddy

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute AS runtime
COPY --from=tls /usr/bin/caddy /usr/bin/caddy
COPY --from=downloads /tmp/packages-microsoft-prod.deb /tmp/packages-microsoft-prod.deb
RUN dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends libmsquic \
    && rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb
WORKDIR /app
# The debug world's asset package is the world directory the composed worlds join. Machine content paths are relative
# to it; the exact image digest retains the cartridges and other dependencies with the runtime.
COPY --from=build /source/worlds/debug ./worlds
COPY --from=build /app .
# Each extension is published with its dependency manifest into a matching directory, so the loader discovers
# only its entry assembly. Both entry points scan ./extensions from /app/worlds; the silo stays provider-neutral.
COPY --from=build /puck-cli /puck-cli
# The deployment supplies its silo document and federation keys as mounted files.
WORKDIR /app/worlds
ENTRYPOINT ["dotnet", "/app/Puck.World.Silo.dll"]
CMD ["--silo", "/configuration/silo.json"]
