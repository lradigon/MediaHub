FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/MediaHub.Api/MediaHub.Api.csproj \
    && dotnet restore src/MediaHub.Worker/MediaHub.Worker.csproj
RUN dotnet publish src/MediaHub.Api/MediaHub.Api.csproj -c Release -o /out/api --no-restore \
    && dotnet publish src/MediaHub.Worker/MediaHub.Worker.csproj -c Release -o /out/worker --no-restore \
    && chmod -R a+rX /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS tools
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl ffmpeg handbrake-cli \
    && case "$TARGETARCH" in \
        amd64) package="megacmd_2.6.0-2.1_amd64.deb"; checksum="56828022459124f8ffe0276e5218718c1eda78f0299aded4eb5a159e9b8037b9" ;; \
        arm64) package="megacmd_2.6.0-1.1_arm64.deb"; checksum="2b3e97fda1e99fa89460ae9513b4bbf6a33e340aead7af09b2707c603243ea4d" ;; \
        *) echo "Unsupported architecture: $TARGETARCH"; exit 1 ;; \
    esac \
    && curl -fsSL "https://mega.nz/linux/repo/xUbuntu_24.04/$TARGETARCH/$package" -o /tmp/megacmd.deb \
    && echo "$checksum  /tmp/megacmd.deb" | sha256sum -c - \
    && apt-get install -y --no-install-recommends /tmp/megacmd.deb \
    && rm -rf /var/lib/apt/lists/* /tmp/megacmd.deb \
    && mkdir -p /app /work /state /home/app/.megaCmd \
    && chown -R app:app /app /work /state /home/app
RUN chmod 755 /home/app
ENV HOME=/home/app
WORKDIR /app
USER app

FROM tools AS api
COPY --from=build --chown=app:app /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD curl -fsS http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "MediaHub.Api.dll"]

FROM tools AS worker
COPY --from=build --chown=app:app /out/worker .
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD test -n "$(find /state/worker-heartbeat -mmin -1 2>/dev/null)" || exit 1
ENTRYPOINT ["dotnet", "MediaHub.Worker.dll"]