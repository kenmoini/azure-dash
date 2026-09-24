# syntax=docker/dockerfile:1
# Build stage runs on the build host's architecture and cross-compiles for TARGETARCH (no QEMU needed).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AzureDash/AzureDash.csproj src/AzureDash/
RUN dotnet restore src/AzureDash/AzureDash.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/AzureDash/AzureDash.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="azure-dash" \
      org.opencontainers.image.description="Azure Workload Identity demo dashboard for AKS and OpenShift" \
      org.opencontainers.image.source="https://github.com/kenmoini/azure-dash" \
      io.openshift.expose-services="8080:http"
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    AUTH_MODE=auto \
    AZURE_CACHE_TTL_SECONDS=60 \
    CONTROLS_ENABLED=true \
    LOG_LEVEL=Information \
    AZURE_DEBUG=false
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 CMD ["dotnet", "/app/AzureDash.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/AzureDash.dll"]
