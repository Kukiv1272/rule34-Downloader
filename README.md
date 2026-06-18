# rule34 Downloader

*[Читать на русском](README.ru.md)*

A Windows desktop application for batch-downloading images, GIFs, and videos from rule34.xxx by tags, using the official XML API.

## Features

- Tag-based batch downloading — each folder inside the chosen directory is treated as a separate tag to fetch
- Automatic sorting into subfolders by file type: `Images` / `Gif` / `Video`
- Indexing of already-downloaded posts (`downloaded.txt`) — re-running the app won't re-download what you already have
- **"Skip existing files"** option and a **Dry-run** mode (preview without downloading)
- Configurable number of parallel download threads (1–16)
- Optional authentication via API Key + User ID (raises rule34.xxx API rate limits)
- One-click check for site and API availability
- Index rebuild from files already on disk (in case `downloaded.txt` is lost or corrupted)
- Ability to stop a download at any time without losing progress
- Progress bar and detailed log console showing the status of every file

## Screenshot
<img width="906" height="633" alt="r34_downloader_2026 18 06 09;37;30" src="https://github.com/user-attachments/assets/fc4d8c89-e7de-4929-910c-d69c50ca5cd2" />

## Requirements

- Windows 10/11 x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (if using the framework-dependent build — see below)

## Installation

Download the prebuilt `.exe` from [Releases](../../releases), or build it from source.

### Building from source

```bash
git clone https://github.com/<your-username>/<repo-name>.git
cd <repo-name>/R34Downloader
dotnet publish -c Release
```

The resulting executable will be at:
```
R34Downloader\bin\Release\net8.0-windows\win-x64\publish\r34_downloader.exe
```

By default this uses a framework-dependent single-file build — the `.exe` is around 1–3 MB, but requires **.NET 8 Desktop Runtime** to be installed on the machine running it.

If you want a fully self-contained build with no external dependencies (at the cost of a much larger file, ~150 MB), change the following in `R34Downloader.csproj`:
```xml
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
```

## Usage

1. Create a folder, and inside it create one subfolder per tag you want to download (e.g. `aaa`, `bbb`, `some_artist`) — each subfolder name is used as a search tag on rule34.xxx
2. Point the **"Tags / download folder"** field at that folder
3. *(optional)* Enter your **API Key** and **User ID** from your rule34.xxx account settings to raise API rate limits
4. Configure thread count and options
5. Click **"START DOWNLOAD"**

Folder structure after downloading:
```
chosen_folder/
├── some_artist/
│   ├── downloaded.txt
│   ├── Images/
│   ├── Gif/
│   └── Video/
└── another_tag/
    ├── downloaded.txt
    └── ...
```

### Other buttons

- **CHECK ACCESS** — verifies that rule34.xxx and its API are reachable, and validates the entered API key
- **REBUILD INDEX** — rebuilds `downloaded.txt` for every tag based on the files actually present on disk (useful if the index file was lost)
- **STOP** — cancels the current operation

## Tech stack

- .NET 8, C#
- WPF (Windows Presentation Foundation)
- rule34.xxx official XML API (`/index.php?page=dapi&s=post&q=index`)

## Disclaimer

This project is intended for personal use and content archiving. Make sure to comply with rule34.xxx's terms of service and the laws of your country when using this tool. Content accessible through the API is adult (18+) content.

## License

MIT — do whatever you want with the code, at your own risk.
