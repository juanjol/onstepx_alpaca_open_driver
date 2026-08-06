# Development notes

State of the work, the decisions behind it, and the traps that cost real time. Read this
before changing anything in the protocol or device layers.

## Current state

Phases 0 to 8 are complete. All four ASCOM devices are implemented and **pass Conform
Universal with zero issues** against the built in simulator.

| Device | Interface | Conform |
| --- | --- | --- |
| Telescope | `ITelescopeV4` | 0 issues |
| Focuser | `IFocuserV4` | 0 issues |
| Rotator | `IRotatorV4` | 0 issues |
| ObservingConditions | `IObservingConditionsV2` | 0 issues |

461 unit tests pass on Linux. The full solution, including the `net48` COM shim, builds
on Linux.

### Still to do

- **Phase 9**: tray icon with `H.NotifyIcon`, Windows service via `UseWindowsService`,
  systemd via `UseSystemd`. Selectable in the installer.
- **Phase 10**: COM local server. `net48` target is already validated against the
  required packages. Read `ASCOM.COM.LocalServer` and `OmniSimCOMProxy` in
  `ASCOMInitiative/ASCOM.Alpaca.Simulators` before deciding the registration mechanism.
- **Phase 11**: Inno Setup installer, `linux-x64` and `linux-arm64` packages, release
  publishing.

## Environment

The .NET 8 SDK lives in `~/.dotnet` and is **not on PATH**. Every command needs:

```sh
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
```

Build and test:

```sh
dotnet build OnStepX.CrossPlatform.slnf -c Release   # Linux subset
dotnet build OnStepX.Ascom.sln -c Release            # everything, COM shim included
dotnet test tests/OnStepX.Core.Tests
```

Run the server against the simulator, then point Conform Universal at it:

```sh
dotnet run --project src/OnStepX.AlpacaServer -c Release -- --simulate
tools/conformu conformance "http://127.0.0.1:11111/api/v1/telescope/0" -n /tmp/conform.log
```

`tools/` holds Conform Universal 4.4.0 for Linux and is gitignored.

## Architecture in one page

- `OnStepX.Core` knows the protocol and nothing about ASCOM. Transports, framing, the
  LX200 formats, the status parser, port autodiscovery, the simulator, the settings model
  and the shared connection.
- `OnStepX.Devices` implements the four ASCOM interfaces on top of Core.
- `OnStepX.AlpacaServer` hosts them behind `ASCOM.Alpaca.Razor`, which supplies the whole
  REST surface, UDP discovery and the management API, plus the Blazor setup pages.
- `OnStepX.Core/Configuration/ControllerConfiguration` reads and writes the settings the
  **controller** keeps, which is a different thing from the driver settings file and is why
  the setup pages separate the two visually. It knows nothing about ASCOM, so it is tested
  against the simulator like the rest of Core.
- `OnStepX.ComShim` is a `net48` COM local server that will talk to the Alpaca server over
  localhost rather than to the mount, so there is one implementation of the device logic
  and no contention for the serial port.

Two decisions worth not relitigating:

**One shared connection, reference counted per device.** `OnStepXConnection` opens the
transport for the first device that connects and closes it for the last one that leaves.
`OnStepChannel` serialises commands, so all four devices work over a single serial port.
This is why no external hub is needed. The count is **per device, not per call**, because
many clients set `Connected = true` more than once and `false` only once.

**Background polling with cached snapshots.** ASCOM properties are synchronous and clients
read them in tight loops. One poll cycle publishes a coherent snapshot, so reads cost no
serial traffic and a client never sees a right ascension and declination that were true at
different instants. Any command that changes state must refresh or invalidate the snapshot
immediately afterwards. That includes writes from the setup UI, which the polling loop
knows nothing about: `ControllerConfiguration` takes an invalidation callback and the server
wires it to every registered device.

**Two kinds of setting, kept visibly apart.** The driver settings file holds what belongs to
the driver, such as which port to open. The controller's own non volatile memory holds the
site, the limits, the park position and the rest. The old WinForms dialogs mixed them, so it
was impossible to tell whether pressing Save had changed the mount or only the driver. The
setup pages say which is which on every card, and firmware values apply immediately while
driver values wait for Save.

## Traps that cost real time

Each of these was a genuine bug, most of them found by Conform Universal.

### Protocol and firmware

- **A frame is limited to 79 characters, not 80.** `Buffer::add` clamps with
  `cbp > bufferSize - 2`, and beyond that the firmware **silently overwrites the last
  character**. `OnStepFraming` fails explicitly instead.
- **With error correction on, every command replies and always ends in `#`**, including
  the ones that send nothing at all in plain mode. That is why the checksum mode simplifies
  the channel rather than complicating it.
- **The firmware discards space, LF and CR while parsing**, so a payload containing them
  arrives mangled with no warning.
- **`:GU#` has inverted flags.** `n` means not tracking, `N` means no goto, `p` means not
  parked. The absence of `n` is what means the mount is tracking.
- **In `:GU#` the `s` flag never appears alone**, only paired with `r` or `t` to mean
  single axis. Reading it on its own gives false conclusions.
- **Tracking rate characters are only emitted while compensation is off.** With
  compensation active the rate is not observable and has to come from `:GT#`. Defaulting to
  sidereal would be inventing data.
- **`:GU#` and `:Gm#` use different letters for the same thing.** East is `T` in one and
  `E` in the other, and `E` in `:GU#` means GEM. Do not mix the parsers.
- **Longitude is west positive in OnStep**, the opposite of ASCOM. Converted in exactly one
  place, `SiteLongitude`.
- **The UTC offset from `:GG#` is added to local time to reach UT1**, the negative of the
  usual timezone value. And OnStep never applies daylight saving: its clock is always
  standard time.
- **Focuser commands come in two scales.** Upper case is microns, lower case is raw steps.
  ASCOM positions are steps, so the driver uses only the lower case forms and reports
  `:Fu#` as `StepSize`. Getting this wrong reports plausible positions that are wrong by
  the microns per step factor, and autofocus converges on the wrong place.
- **OnStep keeps one rate per axis, shared between pulse guiding and manual moves.** So
  `MoveAxis` overwrites the guide rate and has to restore it, or the next `PulseGuide` runs
  at slew speed.
- **West lowers right ascension**, because hour angle is sidereal time minus right
  ascension.
- **In an integrated mount build, `:hP#` and `:hR#` park the mount**, and the focuser and
  rotator handlers never see them. Only standalone or remote node builds route them to the
  accessory.
- **An unimplemented command is indistinguishable from a value, unless the format says
  otherwise.** With error correction on, the firmware answers every frame, so a command this
  build does not have comes back as the body `0` through the same path a real reply uses.
  What saves it is formatting: the firmware prints decimals, degree marks and signs, so a
  real zero arrives as `0.0` or `+00*` and never as a bare `0`. Plain integer fields such as
  `:GXE9#` and `:%BR#` have no such marker, and zero minutes of meridian limit or zero
  backlash are ordinary values, so those report what the firmware said. Where a subsystem's
  presence actually matters it is established once, at the section level: PEC from `:GU#`,
  the rotator from `:GX98#`, the focuser from `:Fa#`, the sensors from the decimal rule.
  Reading `:GE#` for `CE_CMD_UNKNOWN` looks like the way out, but the reference does not say
  the error code is cleared per command, so a stale failure would turn a legitimate zero into
  "not supported", which is the worse mistake.
- **Do not retry an optional read.** Retries exist to survive line noise, and a command the
  firmware does not implement will not start existing on the second attempt. Leaving them on
  multiplies the cost of every absent field by the retry count, which is how a diagnostics
  page turns into a minute of waiting on real hardware. `OnStepChannel.TryGetStringAsync`
  exists for exactly this.
- **`:$QZ?#` reports the PEC states with completely different characters from `:GU#`.** The
  same five states are `I p P r R` in one and `/ , ~ ; ^` in the other. Reusing either parser
  for the other command yields "unknown" for every value.
- **Tracking compensation takes two commands, in one order only.** `:Tn#`, `:Tr#` and `:To#`
  choose the model and reset the axis count to dual, then `:T1#` or `:T2#` chooses the axis
  count. The other order silently loses the single axis choice.
- **Write the UTC offset before the clock.** The firmware interprets the local time it is
  given against the offset it currently holds, so setting the time first and the offset second
  stores a time wrong by the difference between the two offsets.

### ASCOM semantics

- **`Slewing` must include `MoveAxis` motion.** OnStep reports "no goto active" during a
  manual move, so the driver tracks it separately. Also alt az convergence, see below.
- **A focuser `Move` outside its travel must be clamped, not rejected**, and moving while
  temperature compensation is on **is allowed** from interface version 3. Reasoning from
  first principles gives the opposite answer on both counts.
- **Rate offsets may only be written while the drive rate is sidereal.** Anything else must
  throw `InvalidOperationException`.
- **`RightAscensionRate` is in seconds of right ascension per sidereal second** while
  OnStep uses arcseconds, a factor of fifteen apart. `DeclinationRate` needs no conversion.
- **An absent sensor must throw `PropertyNotImplementedException`, never return zero.**
  Zero degrees, zero humidity and a zero dew point are all believable, so a client acts on
  them, and a false dew point closes an observatory roof for no reason.
- **`PreventRemoteDisconnects` must stay off.** With it on, the REST layer swallows
  `Connected = false` and the device still reports connected, which breaks the contract.
- **`SlewToAltAz` needs convergence on an equatorial mount.** The mount slews to a fixed
  equatorial position while the sky keeps turning, so a single conversion lands about 150
  arcseconds out. The driver re-aims until it is within tolerance and keeps `Slewing` true
  throughout, because ASCOM promises arrival when it clears.
- **`DestinationSideOfPier` has to save and restore the target**, since `:MD#` reports the
  destination for whatever target is currently set.

### Our own code

- **`Path.GetFullPath` does not resolve symlinks.** In sysfs, `/sys/class/tty/<tty>/device`
  is always a symlink, so walking up from the link path never finds `idVendor`. This
  affected Linux only and would have failed on a Raspberry Pi.
- **`atan2` arguments must share a scale.** The horizontal to equatorial conversion had one
  argument carrying a `cos(latitude)` factor and the other not, so it was only correct on
  the equator or due north and south. Every alt az slew landed degrees away. Both
  conversions now use the plain spherical triangle form with no algebraic shortcuts.
- **Do not read a cancellation token field inside a queued lambda.** Connecting and
  immediately disconnecting nulled the field before the task ran, and the poll loop
  dereferenced null on a thread pool thread.
- **Clear the channel's own read buffer when discarding input.** Block reads can leave part
  of a late reply in the channel buffer, and the next transaction would read it as its own.
- **A device must check `Connected` explicitly.** Relying on the channel to throw is not
  enough: a device whose reads all come from a cache answers happily while disconnected.
- **`with` on a record is a shallow copy.** `OnStepXSettings` has nested sections, so a page
  that binds a `with` copy is still editing the live settings by reference: a half typed baud
  rate would already be in force for the next connection and cancelling would be impossible.
  `SettingsStore.Clone` goes through the serializer so the copy is complete.
- **The setup UI must never hold the connection open.** It borrows it per operation under its
  own device key and releases it in a `finally`. A browser tab can vanish at any moment, and a
  dead Blazor circuit holding the port would keep the serial port open until the server was
  restarted. Because the connection is reference counted, borrowing it also means a setup page
  never interrupts a client that is already working.
- **Port discovery has to be blocked while a client is connected.** It opens serial ports one
  by one, and one of them is the port in use.

### Blazor and Razor

- **A component attribute string literal cannot span lines.** Long hint text has to be one
  physical line, or come from a property in the code block. Interpolated multi line attributes
  produce a cascade of unrelated parse errors that hides the real one.
- **Using a named child fragment forces every other fragment to be named too.** A component
  with both `Actions` and `ChildContent` needs `<ChildContent>` written out explicitly, or the
  compiler reports the tag as malformed.
- **`IProgress<T>` callbacks arrive on a thread pool thread.** Port discovery reports progress
  from whichever probe finished, so a redraw has to go through `InvokeAsync` or the progress
  list silently never moves.
- **`step` on a number input is validated, not just a spinner increment.** A latitude of
  40.4167 against `step="0.0001"` is a step mismatch and the browser marks the field invalid,
  so free decimal fields use `step="any"`.

## Simulator

`FakeOnStepDevice` implements `ITransport` and answers the command set, which is what makes
conformance checking possible with no hardware on any platform. It is deliberately faithful
rather than convenient:

- Its clock runs with the real one. A frozen clock made sidereal time drift hours away from
  reality.
- Axes move over time. Rate offsets really drift, pulse guides really move, manual moves
  really move.
- Pier side is **derived from hour angle**, not stored, because that is how a German
  equatorial behaves and a stored value cannot express it.
- `IsSlewing` deliberately ignores raw axis motion, matching the firmware, which reports no
  goto during a manual move or a pulse guide. ASCOM forbids pulse guiding from setting
  `Slewing`.
- Microns per step is 1.13507, deliberately not 1, so any confusion between the focuser's
  two scales shows up as a clearly different number.
- The rotator moves at 12 degrees per second. See the comment on that field: a realistic
  rate is required because crossing the mechanical limit turns a 45 degree ASCOM move into a
  315 degree mechanical sweep.

## Conventions

- All code, comments, documentation and commit messages in **English**.
- **Never** use an em dash. Do not use a hyphen as prose punctuation either.
- Comments explain why, not what. Several document firmware quirks or bugs that were fixed;
  keep that reasoning.
- Do not commit the `.claude` directory or `CLAUDE.md`.
- `docs/COMMAND_REFERENCE.md` is the authoritative command set, taken from the OnStepX
  source tree. `docs/ONSTEP_WIKI_PROTOCOL.txt` documents classic OnStep and disagrees with
  it in places, notably the 40 character command limit; the former wins.
- `docs/SETUP_UI_CHECKLIST.md` is the field by field record of what the setup pages have to
  cover, taken from the two old WinForms dialogs plus the new OnStepX sections. It is the
  acceptance criterion for phase 8 and the place to look before adding a field.
