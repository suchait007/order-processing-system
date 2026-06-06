FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5116

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY StoreApi.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish StoreApi.csproj -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5116
ENV ASPNETCORE_ENVIRONMENT=Development
ENTRYPOINT ["dotnet", "StoreApi.dll"]
