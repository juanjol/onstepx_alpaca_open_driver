# User guide

This is what to do once the driver is installed. If it is not installed yet,
start with [INSTALLING.md](INSTALLING.md).

Everything here happens through the setup page the server publishes at
`http://localhost:11111/`, or at the machine's address and that port if the
server is on a Raspberry Pi at the telescope.

## How it fits together

There is one server and one connection to the controller. The mount, the
focuser, the rotator, the environmental sensors and the auxiliary features are
five ASCOM devices, but they all reach the same OnStepX controller over the
same cable. The server opens the port for the first device that connects and
closes it for the last one that leaves, so there is no need for a serial port
splitter or a hub, and no risk of two clients fighting over the port.

Clients reach those five devices either over Alpaca, straight over the
network, or over COM through the shim the Windows installer registers. Both
routes end up at the same server.

## Finding the mount

You do not normally have to tell the driver which serial port the controller
is on. Autodiscovery is on by default, and it is also what happens regardless
when no port has been chosen yet, because there is no alternative.

Discovery does not simply open every port in turn. It classifies candidates
first, using the USB vendor and product identifiers, and prefers the bridges
OnStepX hardware actually uses: CP210x, CH340, FTDI, Teensy, ESP32, and
similar. It rules out ports that are known to be a bad idea to open, such as
Bluetooth serial ports and virtual modems, where opening one can block for
seconds or trigger a pairing prompt. Only then does it probe the survivors, in
order, with read only commands and a sweep of baud rates, so nothing is ever
sent to a device that turns out not to be a mount. This works the same way on
Windows and on Linux.

On the **Connection** page, "Scan ports" runs that search and shows what it
found, port by port, as it goes. You can stop it partway. "Use this one"
adopts a result. Scanning is refused while a client is connected, since it
would have to open the very port that client is using.

If you would rather be explicit, turn autodiscovery off and name the port
yourself. The same page also configures the TCP transport, for controllers
reached over the WiFi addon rather than a cable.

## Driver settings and controller settings

This is the distinction worth understanding before you change anything,
because the two look similar on screen and behave completely differently.

**Driver settings** live in the driver's own JSON settings file: which port to
open, the baud rate, the timeout, the poll interval, which focuser to select,
your aperture and focal length, the Alpaca port. They can be edited with no
mount attached. They are written when you press Save, and they take effect
**the next time the driver connects**.

**Controller settings** live in the OnStepX controller's own non volatile
memory: the site latitude and longitude, the clock, the horizon and meridian
limits, backlash, the park position, PEC, the mount type. Changing one of
these needs a live connection, and it applies **immediately**, to the mount
itself. It also survives reinstalling or replacing the driver entirely,
because the driver never held it in the first place.

The old WinForms setup dialogs mixed the two together, and the result was that
pressing Save gave you no way to tell whether you had just changed your mount
or just changed a preference. The setup pages here keep them visibly apart:
controller values sit in bordered cards with their own Apply button, driver
values sit in plain cards above a save bar that says what it is going to
write. If a card has its own Apply button, you are talking to the mount.

Anything destructive on the controller side, such as resetting at home,
replacing the park position, clearing the PEC buffer, changing the mount type,
restarting the controller or clearing its memory, asks you to type a
confirmation word rather than offering a button you can hit by accident.

## The setup pages

The navigation bar has one entry per area.

**Dashboard.** The state of the connection, which devices are currently
holding the port, what the controller reports about itself, and the last
error if there was one. A "Test connection" button, and the table of the five
devices with their Alpaca addresses. If the server was started in simulated
mode, this page says so loudly.

**Connection.** How to reach the controller: serial or TCP, port, baud rate,
timeout, error correction, retries per command, poll interval, and the port
scan described above. All driver settings.

**Mount.** The largest page. Site latitude, longitude and elevation, and the
controller's clock, all read from and written to the controller. A "set from
this computer" button handles the clock and the UTC offset together, which is
the safe route, because OnStep never applies daylight saving and its clock is
always standard time. Then the limits (horizon, zenith, meridian east and
west), backlash, and maximum goto rate. Then the OnStepX specific sections:
meridian flip behaviour, axis limits, tracking compensation and rate offsets,
goto rate presets, homing, parking, PEC, the buzzer, and an advanced section
with the mount type and the destructive controller operations. Aperture, area,
focal length and "set date and time on connect" are driver settings and sit
apart from the rest.

**Focuser.** Which of the up to six focusers the driver uses, its current and
limit positions, backlash and deadband, DC motor power, temperature
compensation and its coefficient, and the actions: return home, set current
position as zero or as home. Everything positional here is a count of
**steps**; microns per step is shown only so you can relate a step count to a
physical distance. The focuser selection applies when the driver next
connects, deliberately, because switching the active focuser under a connected
client would silently point it at different hardware.

**Rotator.** Mechanical angle, travel limits, degrees per step, backlash,
derotation on and off and its direction, moves to the parallactic angle or to
half travel, and the sky angle offset. Note that the ASCOM reverse setting and
the firmware's derotation reverse are two different things, and both are on
the page, separately.

**Weather.** Temperature, pressure, humidity and dew point as the controller
reports them, plus the controller's own temperature, the averaging period, and
the option to push readings from an external weather station into the
controller. A sensor that is not fitted shows as not supported. It never shows
zero, because a zero dew point is believable and a client that acts on it
might close a roof for nothing.

**Switch.** The controller's eight auxiliary feature slots, which is where dew
heaters, fans, flat panels and camera shutter releases live. What each slot is
was decided when the firmware was built, with the `FEATUREn_PURPOSE` settings,
so this page shows what the controller reports rather than letting you create
a slot. Switches and analog outputs can be operated here as well as from a
client. A dew heater shows whether its ramp is running, the two temperatures
that define the ramp, and how far above the dew point its sensor currently is.

Two things on this page are worth knowing.

The heater's two ramp temperatures are here and **not** offered to clients.
The controller runs the heater itself: at the "full power at" temperature the
heater runs flat out, at the "switches off at" temperature it stops, and in
between the power ramps down. Those two values are the calibration, they live
in the controller's non volatile memory, and ASCOM defines switching a switch
off as writing its lowest value. A client tidying up at the end of a session by
switching everything off would therefore have written the lowest possible
calibration and thrown yours away. Clients get to start and stop the heater,
which is what they actually need.

The controller also keeps the two temperatures in order, so if you set the
"full power at" value above the "switches off at" value it will quietly move
one of them. The page reads the slot back after every write for exactly that
reason: what you see afterwards is what the controller kept, not what you
typed.

Some slots appear on this page and deliberately not in a client's switch list,
and the page tells you which and why. A camera shutter release is one: the
controller reports the frame counters but never whether a sequence is running,
so a switch for it could be written and never honestly read. A slot configured
as a hidden switch is the other: the controller reports it as present, refuses
to report its state, and reports success for writes it never carries out.

**Diagnostics.** Firmware identification and build, mount type, instrument
angles, encoder counts, steps per degree, step frequencies, stepper driver
status and StallGuard telemetry, MCU temperature, the last command error, and
the channel's own transaction and retry counters. Each section is read on
demand rather than on page load, because a full pass is dozens of commands and
that is instant against the simulator but several seconds on a real serial
link.

**Settings file.** The server's own settings: Alpaca port, site description,
remote access, discovery, Swagger, authentication, and the export and import
described below.

## Connecting an Alpaca client

Clients that speak Alpaca natively, NINA being the obvious one, find the
server by themselves. The server answers Alpaca discovery over UDP, so as long
as the client is on the same network, all five devices appear in its device
list with no IP address or port typed anywhere.

Each device answers at device number zero, since there is only ever one mount
behind one controller.

This is the route to prefer when the server runs on a Raspberry Pi at the
telescope: nothing has to be installed on the imaging computer at all.

## Connecting a COM only client

Clients that only speak COM, such as PHD2, Cartes du Ciel and SGP, use the COM
shim. The Windows installer registers it for you, always, whichever startup
option you picked.

After installing, five entries appear in the ASCOM Chooser like any other COM
driver:

* `OnStepX Telescope`
* `OnStepX Focuser`
* `OnStepX Rotator`
* `OnStepX Observing Conditions`
* `OnStepX Switch`

Pick the one the client asks for and that is the whole configuration. The shim
has no settings of its own: it reads the Alpaca port out of the server's own
settings file, so changing the port on the setup page is enough for COM
clients to follow. It talks to the server over the local machine's loopback
address, not to the mount, which is why there is only ever one implementation
of the device logic and no contention for the serial port.

The shim requires the Alpaca server to be running. If you chose "launch
manually" at install time, start it before the COM client tries to connect.

ASCOM Platform 7's Alpaca aware Chooser is an alternative route for the same
clients, if you would rather not use the shim.

## Exporting your configuration

The **Settings file** page can export the whole driver configuration as JSON,
either as a download or as text to copy. The export is itself a valid settings
file, so it is the way to move a working setup to another machine, keep a copy
before changing something, or hand your configuration to somebody trying to
reproduce a problem.

The download comes from `/settings/export` and arrives as
`onstepx-settings.json`.

The authentication password is deliberately left out of an export, since an
export is meant to be copied around. Type it again on the machine that imports
it; an import with no password in it keeps whatever password was already
configured there.

Import is on the same page. The file is validated before anything is replaced,
and importing replaces the whole configuration, connection settings included,
so expect to check the port afterwards if the two machines are not identical.

Note that an export covers driver settings only. Everything the controller
keeps in its own memory stays with the controller, which is the point of that
distinction.

## When something does not work

**Look at the log output.** The server logs to the console, and it has no log
file of its own anywhere. Where that output ends up depends on how it is
running:

* Started with `--console`, or from the Windows Start Menu entry, it is in the
  console window in front of you. This is the most informative way to run it
  while diagnosing something, on either platform.
* As a systemd service on Linux, it goes to the journal:
  `journalctl -u onstepx-alpaca -f`.
* As a Windows service, there is no console to write to. The .NET Windows
  service integration routes log output to the Windows Event Log, but the
  practical approach is to stop the service and run the executable once with
  `--console` to see the whole picture.

**The Dashboard reports the last connection error** and the Diagnostics page
reports the controller's last command error and the channel's retry counter, a
climbing retry count being a good sign of a marginal cable or the wrong baud
rate.

**Try `--simulate`.** Starting the server with that flag gives you a fully
working system with a simulated controller behind it. If the client works
against the simulator and not against the mount, the problem is in the link to
the controller rather than in the client or the driver. Nothing about a
simulated run is persisted.

**Check that only one copy is running.** Two servers cannot share the Alpaca
port, and two processes cannot share the serial port. This catches people who
install as a service and then also start a copy by hand.
