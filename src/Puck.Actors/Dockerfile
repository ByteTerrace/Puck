FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Puck.Actors.csproj packages.lock.json* ./
RUN dotnet restore ./Puck.Actors.csproj

COPY . .
# The build context may include host bin/obj (az acr build does not honor .dockerignore when it
# packs the context); discard them so publish restores fresh against the warm package cache.
RUN rm -rf bin obj && dotnet publish ./Puck.Actors.csproj --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# 8080: internal HTTP API; 11111: Orleans silo-to-silo; 30000: Orleans gateway.
EXPOSE 8080
EXPOSE 11111
EXPOSE 30000

ENTRYPOINT ["dotnet", "Puck.Actors.dll"]

