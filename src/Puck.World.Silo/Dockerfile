FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
ADD https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz /tmp/dxc.tar.gz
RUN echo '55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54  /tmp/dxc.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/dxc.tar.gz -C /usr/local --strip-components=1 \
    && dxc --version
COPY . .
ENV CI=true
RUN dotnet restore src/Puck.World.Silo --locked-mode \
    && dotnet publish src/Puck.World.Silo -c Release --no-restore -o /app \
    && dotnet run build/Prepare-WorldSilo.cs -c Release -- src/Puck.World/Assets/worlds /app/worlds

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
ADD https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb /tmp/packages-microsoft-prod.deb
RUN echo 'c13f01ac7c3001b51a9281d40dde666db5e037e05512840c319832f7852bfec4  /tmp/packages-microsoft-prod.deb' | sha256sum -c - \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends libmsquic \
    && rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb
WORKDIR /app
COPY --from=build /app .
# The deployment supplies its silo document and federation keys as mounted files.
ENTRYPOINT ["dotnet", "Puck.World.Silo.dll"]
CMD ["--silo", "/configuration/silo.json"]
