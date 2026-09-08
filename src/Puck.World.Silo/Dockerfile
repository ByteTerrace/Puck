FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
ADD https://github.com/microsoft/DirectXShaderCompiler/releases/download/v1.9.2607/linux_dxc_2026_07_29.x86_x64.tar.gz /tmp/dxc.tar.gz
RUN echo '55665c87824051ed4774ff3280a79ccbbb7d39243b9736ca5e98222134112d54  /tmp/dxc.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/dxc.tar.gz -C /usr/local --strip-components=1 \
    && dxc --version
COPY . .
ENV CI=true
RUN dotnet restore src/Puck.World.Silo --locked-mode \
    && dotnet publish src/Puck.World.Silo -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The deployment supplies its silo document and federation keys as mounted files.
ENTRYPOINT ["dotnet", "Puck.World.Silo.dll"]
CMD ["--silo", "/configuration/silo.json"]
