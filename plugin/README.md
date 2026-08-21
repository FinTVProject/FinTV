# FinTV Plugin

Thin [Jellyfin](https://jellyfin.org) 12 plugin that connects to [FinTV Server](https://github.com/binarygeek119/FinTV).

GUID: `f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b`

## Configure

Dashboard → Plugins → FinTV:

- FinTV Server URL (example `http://FinTV-Server:8097`)
- API key (same as `FINTV_API_KEY` on the server)
- Auto-register Live TV
- Write blackframe chapters onto Jellyfin items

Scheduled tasks:

- **FinTV Catalog Sync** — metadata, paths, chapters
- **FinTV Commercial Blackframe Detection**

Manage channels in the FinTV Server Web UI, not this page.
