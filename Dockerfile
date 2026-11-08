# PostGIS API – multi-arch image (linux/amd64, linux/arm64, linux/arm/v7)
#
# Raspberry Pi (64-bit OS, Pi 3/4/5):  docker build -t postgisapi .            (directly on the Pi)
# Cross-build on a PC:                 docker buildx build --platform linux/arm64 -t postgisapi:latest --load .
# Multi-arch push:                     docker buildx build --platform linux/amd64,linux/arm64,linux/arm/v7 -t <registry>/postgisapi:1.0.0 --push .

# The SDK always runs on the build machine's architecture and cross-compiles for the target.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY src/PostGISApi/PostGISApi.csproj src/PostGISApi/
RUN dotnet restore src/PostGISApi/PostGISApi.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/PostGISApi/PostGISApi.csproj -c Release -a $TARGETARCH --no-restore \
        -o /app/publish /p:UseAppHost=false \
    && mkdir -p /app/config

# Chiseled runtime: small, no shell, runs as non-root user "app" (uid 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build --chown=1654:1654 /app/config /app/config

ENV ASPNETCORE_HTTP_PORTS=8080 \
    PostGISApi__ConfigDirectory=/app/config \
    DOTNET_gcServer=0

EXPOSE 8080
VOLUME ["/app/config"]
USER 1654

ENTRYPOINT ["dotnet", "PostGISApi.dll"]
