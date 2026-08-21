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
- PostgreSQL 16 (separate container)
- Jellyfin 12 + the FinTV plugin
- The same media shares mounted into Jellyfin and FinTV Server

## Docker Compose

```yaml
# see docker-compose.yml
```

1. Start Postgres + `ghcr.io/binarygeek119/fintv-server:latest`
2. Open `http://<host>:8097` and sign in (`FINTV_ADMIN_USER` / `FINTV_ADMIN_PASSWORD` on first run)
3. Add path remaps under General
4. Install the FinTV plugin, set Server URL + API key (`FINTV_API_KEY`), run **FinTV Catalog Sync**
5. Join FinTV Server, Postgres, and Jellyfin to the **same Docker network**

Unraid: install official PostgreSQL from CA, then FinTV-Server from the template. Put both on a custom network with Jellyfin.

## IPTV

M3U: `http://FinTV-Server:8097/iptv/channels.m3u?apiKey=...`  
XMLTV: `http://FinTV-Server:8097/iptv/epg.xml?apiKey=...`

The plugin can register these automatically when **Auto-register Live TV** is enabled.

## Weather and news

WeatherStar graphics are vendored from [ws4kp](https://github.com/netbymatt/ws4kp) and [ws3kp](https://github.com/netbymatt/ws3kp) (MIT) and served on loopback inside the container, then encoded to MPEG-TS with optional Jellyfin music as a bed.

News is a 24/7 channel: RSS feeds from the **News** page, optional TTS, FFmpeg overlay, and bed music.

## License

FinTV Server code follows this repository's license. WeatherStar vendors keep their upstream MIT licenses.
