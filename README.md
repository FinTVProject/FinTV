<p align="center">
  <img src="logo.png" alt="FinTV" width="320" />
</p>

# FinTV Server

Simulated live TV for [Jellyfin](https://jellyfin.org). This repository is **FinTV Server** — a .NET 10 Docker app with a red Jellyfin-style Web UI, PostgreSQL, local library playback, WeatherStar, and news.

The Jellyfin plugin (GUID `f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b`) syncs library metadata/paths/chapters, registers Live TV M3U + XMLTV, and runs blackframe chapter detection.

## What runs where

| FinTV Server (this repo) | FinTV Plugin |
| --- | --- |
| Channels, lineups, playout, FFmpeg MPEG-TS | Server URL + API key |
| Commercials / CommercialBrainz | Catalog metadata sync (IDs, tags, duration, **path**, **chapters**) |
| EBS, logos, AI lineups | Live TV tuner + XMLTV registration |
| WeatherStar 4000/3000 (in-image ws4kp/ws3kp) | Blackframe scan + optional write chapters |
| News RSS + TTS channel | |
| Web UI (username/password) | |

Playback reads **local files** from mounted library shares. Configure **path remaps** in Settings (Jellyfin prefix → FinTV prefix), for example `/data/media` → `/media`.

## Requirements

- Docker (Unraid Community Apps template in [`unraid/fintv-server.xml`](unraid/fintv-server.xml))
- PostgreSQL (your own instance)
- Jellyfin 12 + the FinTV plugin
- The same media shares mounted into Jellyfin and FinTV Server

## Docker

Image: `ghcr.io/fintvproject/fintv-server:latest` (built from this repo). Unraid template: [`unraid/fintv-server.xml`](unraid/fintv-server.xml).

```bash
cp .env.example .env
# set FINTV_MEDIA and POSTGRES_* for your existing database
docker compose up -d --build
```

Then:

1. Open `http://<host>:8097` and create the admin username and password on first launch
2. Copy the plugin API key from **General** (created automatically on first boot)
3. Add path remaps under General (Jellyfin prefix → `/media` or your mount)
4. Install the FinTV plugin, set Server URL + API key, run **FinTV Catalog Sync**
5. Join FinTV Server and Jellyfin to the **same Docker network** as your PostgreSQL instance

Items removed from Jellyfin, or whose remapped local file is gone, are marked missing, then deleted by **Tasks → Catalog cleanup** after the grace period (default 7 days). **Scan Local Files** checks each catalog path after remap.

Unraid: point `POSTGRES_HOST` at your existing Postgres container on a custom network with Jellyfin. Pass `/dev/dri` into the container for Intel VAAPI encode/decode (enabled by default as `FFMPEG_HWACCEL=vaapi`).

The plugin registers the Live TV tuner and XMLTV guide automatically when you set the FinTV Server URL and API key.

## Weather and news

WeatherStar graphics are vendored from [ws4kp](https://github.com/netbymatt/ws4kp) and [ws3kp](https://github.com/netbymatt/ws3kp) (MIT) and served on loopback inside the container, then encoded to MPEG-TS with optional Jellyfin music as a bed.

News is a 24/7 channel: RSS feeds from the **News** page, optional TTS, FFmpeg overlay, and bed music.

## License

FinTV Server code follows this repository's license. WeatherStar vendors keep their upstream MIT licenses.
