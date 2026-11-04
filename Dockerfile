FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KgtaAlert.slnx ./
COPY src/Watcher.Api/Watcher.Api.csproj src/Watcher.Api/
COPY src/Watcher.Domain/Watcher.Domain.csproj src/Watcher.Domain/
COPY src/Watcher.Infrastructure/Watcher.Infrastructure.csproj src/Watcher.Infrastructure/
RUN dotnet restore KgtaAlert.slnx
COPY . .
RUN dotnet publish src/Watcher.Api/Watcher.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Watcher.Api.dll"]
