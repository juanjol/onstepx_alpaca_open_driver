# Setup UI field checklist

Acceptance criterion for phase 8. Every field of the two old WinForms setup dialogs had to
survive the redesign, and the new OnStepX sections had to become reachable. This file is the
record, page by page.

Legend for where a value lives:

- **JSON** is a driver setting, stored in the settings file, editable with no mount attached,
  and applied when the driver next connects.
- **Firmware** is read from and written to the controller, so it needs a connection, it
  applies immediately, and it survives reinstalling the driver because it lives in the
  mount's own non volatile storage.
- **Live** is read only telemetry.

Keeping that distinction visible is not cosmetic. The old forms mixed the two, so it was
impossible to tell whether pressing Save had changed the mount or only the driver. Firmware
values sit in bordered cards with their own Apply buttons; driver values sit in plain cards
above a save bar that says what it writes.

## Connection page

| Old field | Where | Status |
| --- | --- | --- |
| Port (COM or IP address) | JSON | done |
| Name / IP address | JSON | done |
| Serial interface, baud rate | JSON | done |
| Timeout, with slider | JSON | done |
| Use Error Correction | JSON | done |
| Connected firmware description | Live | done |
| Trace on | JSON | done |
| Port autodiscovery with progress | action | done, with cancellation and a per port result list |
| Retries per command | JSON | new, done |
| Poll interval | JSON | new, done |

Discovery is refused while a client holds the port, because it opens serial ports one by one
and one of them is the port in use.

## Mount page

| Old field | Where | Status |
| --- | --- | --- |
| Site latitude | Firmware | done |
| Site longitude | Firmware | done, entered positive east and converted for the firmware |
| Site elevation | Firmware | done |
| UTC offset | Firmware | done, written by "set from this computer" |
| Local standard date | Firmware | done |
| Local standard time | Firmware | done |
| UT1 date and time | Live | done |
| Local sidereal time | Live | done |
| Set Date/Time on Connect | JSON | done |
| Aperture diameter | JSON | done |
| Aperture area | JSON | done |
| Focal length | JSON | done |
| Horizon limit | Firmware | done |
| Zenith limit, overhead | Firmware | done |
| Meridian limit east | Firmware | done |
| Meridian limit west | Firmware | done |
| Backlash RA / Azm, arcsec | Firmware | done |
| Backlash Dec / Alt, arcsec | Firmware | done |
| Max goto rate, us per step and deg/s | Firmware | done, the period is editable and deg/s is derived and read only |

The standard time warning is on the page: OnStep never applies daylight saving, so its clock
is always standard time, and the offset it stores is the value to add to local time to reach
UT1. Both are handled by the "set from this computer" button, which is the safe route in
summer.

The UTC offset is deliberately not a free field of its own. It is written together with the
clock, because the firmware interprets the time it is given against the offset it currently
holds and the two have to move together.

## Mount page, new OnStepX sections

| Section | Fields | Status |
| --- | --- | --- |
| Meridian flip | auto flip, home mode direct or visit or pause, preferred pier side, continue after pause | done |
| Axis limits | axis 1 and axis 2 minimum and maximum, read only | done |
| Tracking compensation | off, refraction, full model, each single or dual axis | done |
| Tracking rate offsets | RA and Dec offsets in arcsec per sidereal second | done |
| Goto rate | current, base and fastest period, derived deg/s, five presets | done |
| Home | auto home at boot, sensors fitted, axis offsets, sense reversal, find home, reset at home | done |
| Park | park, unpark, set park position | done |
| PEC | state, play, stop, arm recording, save, clear, worm steps, buffer size, index position | done |
| Buzzer | enable, disable, test beep | done |
| Advanced | mount type for the next restart, restart controller, clear non volatile storage | done |

Everything destructive asks for a typed confirmation word rather than a plain button:
resetting at home, replacing the park position, clearing the PEC buffer, changing the mount
type, restarting, and clearing the controller's memory.

## Focuser page

| Old field | Where | Status |
| --- | --- | --- |
| Focuser selection, default 1, range 1 to 6 | JSON | done, applied on connect |
| Minimum position | Live | done |
| Current position | Live | done |
| Maximum position | Live | done |
| Return home | action | done |
| Set current as zero | action | done, behind confirmation |
| Set current as home | action | done, behind confirmation |
| Microns per step | Live | done, display only |
| Backlash | Firmware | done, in steps |
| Deadband | Firmware | done, in steps |
| DC motor power | Firmware | done |
| Temperature compensation enable | Firmware | done |
| Temperature compensation coefficient | Firmware | done, microns per degree |
| Temperature delta from baseline | Live | done |
| Compensation amount | Live | done, derived from the coefficient and the delta since no command reports it |
| Move to position on connect | JSON | new, done |

Two things are stated on the page because they are the expensive confusions here. Every
position, limit, backlash and deadband is a count of **steps**, and microns per step exists
only to relate a step count to a physical distance. And the focuser selection is applied when
the driver connects rather than switched live, because switching the active focuser would
silently redirect a connected client to different hardware.

The old form's "focuser selection" is listed in the plan as driver plus firmware. Only the
driver half is implemented, deliberately, for the reason above.

## Rotator page

| Field | Where | Status |
| --- | --- | --- |
| Minimum and maximum angle | Live | done |
| Degrees per step | Live | done |
| Current mechanical angle | Live | done |
| Backlash, steps | Firmware | done |
| Capability, derotate or rotate only or none | Live | done |
| Reverse direction | JSON | done |
| Derotation on and off | Firmware | done |
| Derotation direction | action | done, a toggle because the firmware does not report the direction back |
| Move to parallactic angle, half travel | action | done |
| Set current as half travel, set current as zero | action | done, behind confirmation |
| Sky angle offset from sync | JSON | done |
| Move to position on connect | JSON | new, done |

The checklist originally listed "reverse direction" as a firmware value. It is not: the ASCOM
`Reverse` property is implemented in the driver, and the only reverse the firmware exposes is
`:rR#`, which flips the **derotation** direction and is a different thing. Both are on the
page, separately.

The angle the firmware reports is mechanical. The sky angle a client sees is that plus the
driver's sync offset, which is why the offset is a driver setting.

## Weather page

| Field | Where | Status |
| --- | --- | --- |
| Temperature, pressure, humidity, dew point | Live | done |
| Sensor presence per property | Live | done |
| Controller temperature | Live | done |
| Average period | JSON | done |
| Push external weather to the controller | JSON | done, with the three values to push |

An absent sensor shows as not supported. It never shows zero, because a zero dew point is
believable and a client acts on it, and a false one closes an observatory roof for no reason.

## Switch page

Added in phase 12. There was no old WinForms equivalent: the auxiliary features were not
reachable at all before this, which is why this page has no "old field" column.

| Field | Where | Status |
| --- | --- | --- |
| Slot number, name and purpose, per configured slot | Live | done |
| Switch state, with on and off buttons | Firmware | done |
| Analog output level, 0 to 255, shown also as a percentage | Firmware | done |
| Dew heater ramp running, with start and stop buttons | Firmware | done |
| Dew heater full power at, -5 to 20 degrees | Firmware | done |
| Dew heater switches off at, -5 to 20 degrees | Firmware | done |
| Dew heater delta above the dew point | Live | done |
| Intervalometer frame counters, exposure and delay | Live | done, read only |
| Slots present but not offered to clients, with the reason | Live | done |
| Read interval | JSON | done |

Three things about this page are deliberate.

**The dew heater ramp temperatures are here and are not ASCOM channels.** ASCOM defines
switching a switch off as writing its minimum value, and clients really do walk the whole
switch list doing that at the end of a session. A ramp start whose range is -5 to 20 degrees
would be set to -5 by any of them, destroying the calibration and spending a non volatile
storage cell on it. Clients get to start and stop the heater, which is what they need.

**Every write is followed by reading the whole slot back.** The controller keeps the ramp start
strictly below its end and quietly moves whichever value was not just written, so the number it
kept is frequently not the number that was typed. Showing the typed value would be showing a
fiction.

**Slots the ASCOM device refuses to expose are still listed, with the reason.** An
intervalometer reports its frame counters but never whether a sequence is running, so a switch
for it could be written and never honestly read. A hidden switch reports itself present, then
refuses to report its state and reports success for writes it never carries out. Without this
section, a client showing fewer switches than the page shows slots would look like a bug.

## Diagnostics page

| Field | Status |
| --- | --- |
| Firmware name, version, build date and time, config, hardware | done |
| Mount type and coordinate mode | done |
| Instrument angles, axis 1 and axis 2 | done |
| Encoder counts | done |
| Steps per degree | done |
| Step frequency per axis | done |
| Stepper driver status flags per axis, with the fault meanings | done |
| StallGuard telemetry per axis | done |
| MCU temperature | done |
| Last command error | done |
| Channel counters, transactions and retries | done |

Each section is read on demand rather than on page load. A full pass is dozens of commands
queued behind the polling loop, which is instant against the simulator and several seconds on
a real serial link.

## Settings file page

| Item | Status |
| --- | --- |
| Alpaca port, site description, remote access, discovery, Swagger, strict mode | done |
| Authentication, user name and password | done |
| Export settings as JSON, as a download and as text | done |
| Import settings from JSON, validated before anything is replaced | done |
| Password excluded from the export | done, and an import with no password keeps the existing one |
