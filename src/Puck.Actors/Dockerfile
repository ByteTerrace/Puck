FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
# Build from the repository root: shared targets, analyzers, and Puck.Maths are required.
COPY . .
ENV CI=true
RUN dotnet restore src/Puck.Actors/Puck.Actors.csproj --locked-mode \
    && dotnet publish src/Puck.Actors/Puck.Actors.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# 8080: internal HTTP API; 11111: Orleans silo-to-silo; 30000: Orleans gateway.
EXPOSE 8080
EXPOSE 11111
EXPOSE 30000

ENTRYPOINT ["dotnet", "Puck.Actors.dll"]

