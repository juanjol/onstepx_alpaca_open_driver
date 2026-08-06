# OnStepX ASCOM

ASCOM drivers for mounts, focusers, rotators and environmental sensors
governed by [OnStepX](https://github.com/hjd1964/OnStepX).

The main implementation is a cross platform **ASCOM Alpaca server**. On top
of it sits an optional **Local Server COM** for Windows clients that only
speak COM.

## Status

Under construction. Phase 0 (scaffolding and CI) completed.

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

## Structure

```
src/OnStepX.Core          Extended LX200 protocol, transports, autodiscovery
src/OnStepX.Devices       Implementation of the ASCOM interfaces
src/OnStepX.AlpacaServer  Alpaca server, REST API and configuration UI
src/OnStepX.ComShim       Local Server COM (net48, Windows only)
tests/OnStepX.Core.Tests  Protocol tests, run on any platform
installer                 Inno Setup for Windows
packaging/linux           systemd unit
docs/COMMAND_REFERENCE.md Reference copy of the firmware's command set
```

## Build

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

## License

GPL-3.0-only. See [LICENSE](LICENSE).

This project does not incorporate code from OnStepX nor from other ASCOM
drivers for OnStep. `docs/COMMAND_REFERENCE.md` is a copy of the firmware's
command set documentation, included as an implementation reference.
