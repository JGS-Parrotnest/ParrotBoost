# ParrotBoost Professional

![Build Status](https://img.shields.io/badge/build-passing-brightgreen?style=for-the-badge&logo=github)
![Version](https://img.shields.io/badge/version-1.2.6--stable-blue?style=for-the-badge)
![License](https://img.shields.io/badge/license-MIT-orange?style=for-the-badge)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?style=for-the-badge&logo=windows)
![Framework](https://img.shields.io/badge/framework-.NET%2011.0-512BD4?style=for-the-badge&logo=dotnet)

ParrotBoost Professional is a Windows optimization utility that combines one-click system tuning with live hardware monitoring. The application is built with WPF and is designed for users who want a compact desktop tool for performance adjustments, telemetry visibility, and quick access to common optimization routines.

## Highlights

- One-click boost and restore workflow for supported system tweaks.
- Real-time monitoring of CPU load, GPU load, CPU temperature, and GPU temperature.
- Modern desktop UI with theme switching, rounded window chrome, and localized resources.
- Administrative startup configuration for operations that require elevated privileges.
- Built-in cleanup, tray behavior, and hardware summary panels.

## Requirements

- Windows 10 or Windows 11
- .NET 11 SDK for local development
- Administrator privileges for runtime features that modify system settings
- Internet access only when restoring NuGet dependencies or opening vendor driver pages

## Installation

### End Users

1. Build or publish the application from `Source/ParrotBoost`.
2. Run `ParrotBoost.exe` as Administrator.
3. Allow the application to initialize hardware monitoring on first launch.

### Developers

1. Install the .NET 11 SDK.
2. Clone or extract the repository.
3. Restore and build the project:

```powershell
dotnet restore .\Source\ParrotBoost\ParrotBoost.csproj
dotnet build .\Source\ParrotBoost\ParrotBoost.csproj
```

4. Optionally use the isolated tooling described in [DevKit/README.md](DevKit/README.md).

## Configuration

ParrotBoost stores user-facing preferences through its settings layer. The current application supports:

- Theme switching between light and dark appearance.
- Startup and tray behavior.
- Language selection for English, Polish, German, and Norwegian.
- Toggleable optimization steps for services, memory, NTFS access timestamps, delivery optimization, USB power saving, and related boost actions.

Because some optimizations affect system services, scheduled tasks, and boot configuration, the application should be executed with elevated permissions.

## Usage

### Dashboard

- Review the hardware summary for CPU, GPU, and memory.
- Check the live performance panel for current load and temperature data.
- Use the GPU driver action when an update check indicates that attention may be required.

### Boost Control

- Click `BOOST` to start the optimization sequence.
- Click `STOP` to restore the supported settings changed by the active boost session.
- Watch the status label for the current execution step and operating mode.

### Settings and Cleanup

- Open `Settings` from the title bar to configure startup, language, and optimization behavior.
- Use the cleanup tab to scan selected system folders and remove temporary data.

## Project Structure

- `Source/ParrotBoost` - main desktop application.
- `Source/ParrotBoost.Tests` - automated tests for selected application components.
- `DevKit` - build and diagnostic helper scripts for isolated developer workflows.

## Monitoring Notes

ParrotBoost uses Windows APIs and hardware sensor access to present live telemetry. GPU temperature monitoring depends on vendor-exposed sensors and administrator access. If a specific device does not expose a readable temperature sensor, the UI falls back to `--°C` instead of displaying fabricated data.

## Development

Development workflow details, build orchestration, and validation scripts are documented in [DevKit/README.md](DevKit/README.md).

## License

Copyright © 2026 JGS. This project is licensed under the MIT License. See the repository license file for complete terms.
