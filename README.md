# OnStepX ASCOM

ASCOM drivers for mounts, focusers, rotators and environmental sensors
governed by [OnStepX](https://github.com/hjd1964/OnStepX).

The main implementation is a cross platform **ASCOM Alpaca server**. On top
of it sits an optional **Local Server COM** for Windows clients that only
speak COM.

## Status

A first beta is published, `v0.1.0-beta.1`, with a Windows installer and
Linux and Raspberry Pi packages on the
[releases page](https://github.com/juanjol/onstepx_alpaca_open_driver/releases).
It is still a beta: it has had far more hours against the built in simulator
than against real hardware under a real sky, so treat it accordingly and
report what breaks.

## Getting started

* **[docs/INSTALLING.md](docs/INSTALLING.md)** covers installing on Windows
  with the installer, and on Linux or a Raspberry Pi with the tarball and the
  systemd unit.
* **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)** covers day to day use: finding
  the mount, what each setup page does, connecting an Alpaca client such as
  NINA or a COM only client such as PHD2, and where to look when something
  does not work.

## Devices

| Device | ASCOM interface |
| --- | --- |
| Mount | `ITelescopeV4` |
| Focuser (1 to 6) | `IFocuserV4` |
| Rotator | `IRotatorV4` |
| Environmental sensors | `IObservingConditionsV2` |

## Why Alpaca

ASCOM's official recommendation for new drivers is Alpaca instead of COM.
It also brings three concrete advantages to this project:

* **Genuinely verifiable.** ConformU (Conform Universal) runs natively on
  Linux and validates Alpaca devices against the specification, including
  the discovery protocol. All the device logic is checked before touching
  Windows.
* **No COM registration nor bitness issues.** No regasm, no x86 against
  x64, no need for ASCOM Platform installed to build.
* **Can be deployed next to the mount.** The server runs on a Raspberry Pi
  with the mount over USB, and the Windows client connects over the network.

Clients that only speak COM are still served: NINA has native Alpaca
discovery, and for the rest (PHD2, SGP, CdC) there is the included COM
shim, or ASCOM Platform 7's Alpaca aware Chooser.

## Own features

* **Serial port autodiscovery.** Ranks candidates by VID and PID (CP210x,
  CH340, FTDI, Teensy and ESP32 CDC), rules out Bluetooth and busy ports,
  and probes with read only commands (`:GVP#`, `:GVN#`) sweeping baud
  rates. Works on Windows and on Linux.
* **Position on connect** for focuser and rotator, optional and non
  blocking.
* **Extended OnStepX configuration**: meridian flip, limits, tracking
  compensation, backlash, goto speed, home, park, PEC, buzzer and diagnostics.
* **Exportable configuration** to JSON, portable between installations.

## Development

Start with [docs/dev/DEVELOPMENT_NOTES.md](docs/dev/DEVELOPMENT_NOTES.md):
the state of the work, the decisions behind it, and the firmware and ASCOM
traps that cost real time. Read it before changing anything in the protocol or
device layers.

### Build

The .NET 8 SDK is required.

On Linux or macOS, with the solution filter that excludes the COM shim:

```sh
dotnet build OnStepX.CrossPlatform.slnf
dotnet test OnStepX.CrossPlatform.slnf
```

On Windows, the full solution including the shim:

```sh
dotnet build OnStepX.Ascom.sln
```

### Structure

```
src/OnStepX.Core             Extended LX200 protocol, transports, autodiscovery
src/OnStepX.Devices          Implementation of the ASCOM interfaces
src/OnStepX.AlpacaServer     Alpaca server, REST API and configuration UI
src/OnStepX.ComShim          Local Server COM (net48, Windows only)
tests/OnStepX.Core.Tests     Protocol tests, run on any platform
installer                    Inno Setup for Windows
packaging/linux              systemd unit for the Alpaca server
docs/INSTALLING.md           Installation guide, both platforms
docs/USER_GUIDE.md           Day to day use
docs/dev/                    Developer documentation, see below
```

`docs/dev/` holds the material only a developer needs:

```
DEVELOPMENT_NOTES.md      State of the work, decisions, and the traps
COMMAND_REFERENCE.md      Reference copy of the firmware's command set
ONSTEP_WIKI_PROTOCOL.txt  Classic OnStep protocol, for the prose it explains
SETUP_UI_CHECKLIST.md     Field by field record of what the setup pages cover
```

## License

GPL-3.0-only. See [LICENSE](LICENSE).

This project does not incorporate code from OnStepX nor from other ASCOM
drivers for OnStep. `docs/dev/COMMAND_REFERENCE.md` is a copy of the
firmware's command set documentation, included as an implementation
reference.
