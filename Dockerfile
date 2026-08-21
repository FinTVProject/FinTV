FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY global.json ./
COPY src/FinTv.Server/ src/FinTv.Server/
COPY scripts/ scripts/
COPY logo.png ./
RUN python3 scripts/fetch-binarygeek119-logos.py src/FinTv.Server/wwwroot/logos/binarygeek119 \
    || echo "Logo fetch skipped (offline or rate-limited)"
RUN dotnet publish src/FinTv.Server/FinTv.Server.csproj -c Release -o /app/publish /p:SkipLogoFetch=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
        ffmpeg \
        ca-certificates \
        python3 \
        nodejs \
        npm \
        wget \
        chromium \
        fonts-liberation \
        fonts-dejavu-core \
        intel-media-va-driver \
        i965-va-driver \
        mesa-va-drivers \
        libva2 \
        vainfo \
    && rm -rf /var/lib/apt/lists/* \
    && wget -qO /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp

WORKDIR /app
COPY --from=build /app/publish .
COPY vendor/ /app/vendor/
WORKDIR /app/vendor/ws4kp
RUN npm install --omit=dev
WORKDIR /app/vendor/ws3kp
RUN npm install --omit=dev
WORKDIR /app

ENV FINTV_CONFIG=/config \
    FFMPEG_PATH=/usr/bin/ffmpeg \
    FINTV_YTDLP_PATH=/usr/local/bin/yt-dlp \
    CHROMIUM_PATH=/usr/bin/chromium \
    FFMPEG_HWACCEL=vaapi \
    FFMPEG_VAAPI_DEVICE=/dev/dri/renderD128 \
    PORT=8097

EXPOSE 8097
VOLUME ["/config"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8097/login >/dev/null || exit 1
ENTRYPOINT ["dotnet", "FinTv.Server.dll"]
