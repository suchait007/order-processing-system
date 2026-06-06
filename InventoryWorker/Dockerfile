FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY InventoryWorker.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish InventoryWorker.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_ENVIRONMENT=Development
ENTRYPOINT ["dotnet", "InventoryWorker.dll"]
