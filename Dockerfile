FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FinTv.Server/ FinTv.Server/
COPY scripts/ scripts/
COPY logo.png logo.png
RUN python3 scripts/fetch-binarygeek119-logos.py FinTv.Server/wwwroot/logos/binarygeek119 || true
RUN dotnet publish FinTv.Server/FinTv.Server.csproj -c Release -o /app/publish /p:SkipLogoFetch=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg ca-certificates python3 nodejs npm wget xz-utils chromium fonts-liberation \
    && rm -rf /var/lib/apt/lists/* \
    && wget -qO /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp \
    && chmod +x /usr/local/bin/yt-dlp

WORKDIR /app
COPY --from=build /app/publish .
COPY vendor/ /app/vendor/
WORKDIR /app/vendor/ws4kp
RUN npm install --omit=dev || true
WORKDIR /app/vendor/ws3kp
RUN npm install --omit=dev || true
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8097
ENV FINTV_CONFIG=/config
ENV FFMPEG_PATH=/usr/bin/ffmpeg
ENV FINTV_YTDLP_PATH=/usr/local/bin/yt-dlp
ENV CHROMIUM_PATH=/usr/bin/chromium
EXPOSE 8097
VOLUME ["/config"]
ENTRYPOINT ["dotnet", "FinTv.Server.dll"]
