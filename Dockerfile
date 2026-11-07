FROM node:24-alpine AS frontend
WORKDIR /workspace
COPY package.json package-lock.json ./
RUN npm ci
COPY vite.config.ts vite.sw.config.ts tsconfig.json tsconfig.worker.json ./
COPY src/Watcher.Api/ClientApp src/Watcher.Api/ClientApp
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Watcher.Api/Watcher.Api.csproj src/Watcher.Api/
COPY src/Watcher.Contracts/Watcher.Contracts.csproj src/Watcher.Contracts/
COPY src/Watcher.Domain/Watcher.Domain.csproj src/Watcher.Domain/
COPY src/Watcher.Infrastructure/Watcher.Infrastructure.csproj src/Watcher.Infrastructure/
RUN dotnet restore src/Watcher.Api/Watcher.Api.csproj
COPY . .
COPY --from=frontend /workspace/src/Watcher.Api/wwwroot/dist src/Watcher.Api/wwwroot/dist
COPY --from=frontend /workspace/src/Watcher.Api/wwwroot/service-worker.js src/Watcher.Api/wwwroot/service-worker.js
RUN dotnet publish src/Watcher.Api/Watcher.Api.csproj -c Release -o /app/publish --no-restore

FROM build AS migration
RUN dotnet tool install --global dotnet-ef --version 10.0.4
ENV PATH="$PATH:/root/.dotnet/tools"
ENTRYPOINT ["dotnet", "ef"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/kgta-alert/data-protection-keys \
    && chown -R $APP_UID:$APP_UID /var/lib/kgta-alert \
    && chmod 700 /var/lib/kgta-alert/data-protection-keys
EXPOSE 8080
EXPOSE 8443
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Watcher.Api.dll"]
