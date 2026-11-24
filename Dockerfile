# The Beesharp host (ASP.NET Core + the WebAssembly client) as a Linux image for linux/amd64 and linux/arm64.
# Docker Desktop runs it on Windows and macOS too (Linux containers).
# The SDK stage always runs on the build machine's own platform and cross-publishes for the target architecture,
# so a multi-arch build needs no emulation.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG TARGETARCH
ARG BUILD_SHA=
WORKDIR /src

COPY . .
RUN dotnet publish src/Beesharp/Beesharp.csproj -c Release -a $TARGETARCH -o /app \
      -p:BuildSha="$BUILD_SHA" -p:SkipPackBeesharpCoreSdk=true \
 && dotnet pack src/Beesharp.Core/Beesharp.Core.csproj -c Release -o /app/sdk \
 && cp -r src/Beesharp/scripts /app/scripts \
 && mkdir -p /app/data

# No RUN in this stage: it would need emulation for the foreign architecture.
FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app

# Includes the runtime-compiled scenario scripts (not part of the publish output, see Beesharp.csproj) and the empty data folder.
COPY --from=build --chown=$APP_UID /app .

# appsettings.json listens on https too, which needs a certificate. In the container: plain http on 8080;
# TLS belongs to the reverse proxy in front of it. URLS (no prefix) wins over the Urls key in appsettings.json.
ENV URLS=http://+:8080
EXPOSE 8080

# Run storage (DuckDB). Mount a volume here to keep runs across container restarts.
VOLUME /app/data

USER $APP_UID
ENTRYPOINT ["dotnet", "Beesharp.dll"]
