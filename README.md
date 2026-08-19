# Skorrent

A BitTorrent v1 client library and WPF desktop application written in C#.

## Features

- **Core Library** (`Skorrent.Core`) - Full BitTorrent v1 protocol implementation:
  - BEncoding codec (canonical encoding/decoding)
  - Torrent file parsing (single/multi-file, piece hashes, infohash)
  - Peer wire protocol (handshake, choke/unchoke, piece/block requests)
  - Tracker client (HTTP/HTTPS + UDP per BEP 15)
  - File storage (piece/block read/write, SHA-1 verification)
  - Percent encoding (RFC 3986)

- **WPF Desktop App** (`Skorrent`) - Modern GUI:
  - Torrent list with progress tracking
  - Real-time piece/peer views
  - Activity log with diagnostics
  - Test torrent generator with working public tracker

- **Console App** (`Skorrent.Client`) - Self-test and torrent inspection

## Quick Start

```powershell
# Run WPF desktop app
dotnet run --project Skorrent.WPF

# Run console self-test
dotnet run --project Skorrent.Client

# Inspect a torrent file
dotnet run --project Skorrent.Client -- path/to/file.torrent
```

## Building

```powershell
dotnet build Skorrent.slnx
```

Requires .NET 10 SDK.

## Project Structure

```
Skorrent.slnx
├── Skorrent.Core/           # Class library (core protocol)
│   ├── BEncoding.cs         # BEncoding codec
│   ├── FileStore.cs         # Piece/block storage
│   ├── Peer.cs              # Peer wire protocol
│   ├── PercentEncoding.cs   # URL encoding
│   ├── Torrent.cs           # Torrent parsing
│   └── Tracker.cs           # HTTP + UDP trackers
├── Skorrent.Client/         # Console app
│   └── Program.cs           # Self-test + torrent inspector
└── Skorrent.WPF/            # WPF desktop app
    ├── ViewModels/          # MVVM view models
    ├── Views/               # XAML views + converters
    ├── Services/            # DownloadEngine, FileDialogService
    └── Models/              # TorrentInfo wrapper
```

## Usage

1. **WPF App**: Click "➕ Add Torrent" to load a `.torrent` file, or "🧪 Create Test Torrent" to generate one with a working public UDP tracker.
2. **Console**: `dotnet run --project Skorrent.Client -- file.torrent` prints torrent metadata.

## Supported Protocols

- BEP 3 (BitTorrent Protocol)
- BEP 15 (UDP Tracker Protocol) 
- BEP 23 (Compact Peer Format)
- BEP 14 (Encryption hint)
- BEP 12 (Multi-tracker announce-list - parsing only)

## Limitations

- No magnet link support
- No DHT/PEX/LSD peer discovery
- No UPnP/NAT-PMP port forwarding
- Private tracker authentication not implemented