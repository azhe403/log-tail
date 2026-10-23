<p align="center">
  <img src="assets/icon/logtail-256.png" alt="LogTail" width="128" />
</p>

# LogTail

A lightweight, cross-platform desktop log viewer and real-time log monitoring tool built with Avalonia UI and .NET 10.

## Features

- **Real-Time Log Monitoring**: Automatically tails active log files with minimal CPU and memory overhead.
- **Tabbed Interface & Drag-and-Drop**: Monitor multiple log files simultaneously; simply drag and drop files into the app.
- **High Performance Engine**: Indexed file reading and ring buffering designed to handle large log files smoothly.
- **Session Persistence**: Automatically restores your previous open tabs and application settings on launch.
- **Customizable Appearance**: Clean, fluent desktop interface with light and dark theme support.
- **Cross-Platform**: Runs natively on Windows, Linux, and macOS.

## Installation

Download the latest pre-built standalone binaries for your operating system from the [Releases](https://github.com/azhe403/LogTail/releases) page:

- **Windows**: `win-x64`, `win-arm64` (.zip)
- **Linux**: `linux-x64`, `linux-arm64` (.tar.gz)
- **macOS**: `osx-x64`, `osx-arm64` (.tar.gz)

Extract the archive and run the `LogTail` executable directly—no external runtime required.

## Build from Source

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Git

### Steps

1. Clone the repository:
   ```bash
   git clone https://github.com/azhe403/LogTail.git
   cd LogTail
   ```

2. Restore dependencies and build the solution:
   ```bash
   dotnet build LogTail.slnx
   ```

3. (Optional) Enable the bundled git hooks so commits run the core test suite:
   ```bash
   git config core.hooksPath .githooks
   ```

4. Run the unit test suite:
   ```bash
   dotnet test LogTail.slnx
   ```

4. Launch the desktop application:
   ```bash
   dotnet run --project src/LogTail.UI/LogTail.UI.csproj
   ```

## Usage

- **Open a Log File**: Use `File > Open` or drag and drop any `.log`, `.txt`, or text file directly into the application window.
- **Multi-Tab Viewing**: Each opened log file opens in a dedicated tab. Switch between tabs or close them when done.
- **Pause & Resume**: Toggle real-time tailing on or off as needed.
- **Settings**: Access application preferences and theme options via the settings dialog.

## Project Structure

```
LogTail/
├── src/
│   ├── LogTail.Core/       # Core logging engine: tailing, ring buffer, indexing, persistence
│   └── LogTail.UI/         # Desktop application built with Avalonia UI and ReactiveUI
└── tests/
    ├── LogTail.Core.Tests/ # Unit tests for the core engine and indexing
    └── LogTail.UI.Tests/   # UI view model and interaction tests
```

## License

This project is licensed under the [MIT License](LICENSE).
