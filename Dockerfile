FROM node:22-alpine AS web
WORKDIR /source
COPY src/scpsl-panel-web/package*.json ./
RUN npm ci
COPY src/scpsl-panel-web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api
WORKDIR /source
COPY src/ScpSlPanel.Api/ScpSlPanel.Api.csproj ./
RUN dotnet restore
COPY src/ScpSlPanel.Api/ ./
COPY --from=web /ScpSlPanel.Api/wwwroot ./wwwroot
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_URLS=http://+:8080
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScpSlPanel.Api.dll"]
