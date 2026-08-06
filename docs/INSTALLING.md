# Installing OnStepX ASCOM

OnStepX ASCOM is an Alpaca server. It runs as a background process, talks to
the OnStepX controller over the serial port (or over TCP for the WiFi addon),
and offers the mount, the focuser, the rotator, the environmental sensors and
the auxiliary features to any client on the network. Its configuration lives in a web page the server
itself serves.

There are two supported ways to install it:

* **Windows**, with the installer. This also registers the COM shim, so
  clients that only speak COM see the drivers in their ASCOM Chooser.
* **Linux, including the Raspberry Pi**, with a tarball and a systemd unit.
  This is the arrangement where the server sits next to the mount and the
  imaging computer connects over the network.

Both come from the same place: the
[releases page](https://github.com/juanjol/onstepx_alpaca_open_driver/releases).

Once installed, read [USER_GUIDE.md](USER_GUIDE.md) for what to do next.

---

## Windows

### Download

From the release, take **`OnStepX.Ascom.Setup-0.2.0.exe`**. It carries
everything: the Alpaca server, the COM shim, and the .NET runtime they need,
so there is nothing else to install first.

The installer is 64 bit only.

### Run the installer

It asks for administrator rights and needs them. It writes under Program
Files, it writes the COM registration into the registry, and it may register a
Windows service, none of which a standard user account can do.

By default it installs into `C:\Program Files\OnStepX ASCOM`. The COM shim
goes into a `ComShim` subdirectory of that. The two are kept apart on purpose:
they are built against different .NET versions and carry same named support
assemblies that are not interchangeable.

### Choose how it starts

One wizard page asks "How should OnStepX ASCOM start?" and offers three
choices. Exactly one of them is always selected.

**Run as a Windows service, started automatically at boot.** This is the
default and the right answer for an unattended rig. The installer registers a
service called `OnStepX ASCOM` set to start automatically, and starts it
immediately. It comes back up after a reboot with nobody logged in, which is
the point. There is no window and no tray icon; you reach it through its web
page.

**Run at login with a system tray icon.** Pick this on a desktop machine you
also use for other things, when you want to see that the server is running and
be able to stop it. The installer puts a shortcut in the all users Startup
folder, so the icon appears for whoever logs in, not only for the account that
ran the installer. Right clicking the icon offers "Open setup page" and
"Exit". The last page of the wizard offers to launch it straight away so you
do not have to log out first.

**Do not start automatically, launch manually from the Start Menu.** Nothing
runs until you start it. The Start Menu entry "OnStepX ASCOM" runs the server
in the foreground with a console window, which is also the most informative
way to run it while diagnosing a problem.

The Start Menu entry exists in all three cases, and so does the uninstall
entry next to it.

### COM registration

COM registration happens every time, whichever startup choice you made. COM
clients are independent of how the Alpaca server itself is hosted, so there is
no case where skipping it would be right. After installing, five drivers named
`OnStepX Telescope`, `OnStepX Focuser`, `OnStepX Rotator`,
`OnStepX Observing Conditions` and `OnStepX Switch` are available in any ASCOM
Chooser.

### Where the settings file ends up

```
%ProgramData%\OnStepX ASCOM\settings.json
```

which is normally `C:\ProgramData\OnStepX ASCOM\settings.json`. One file for
the whole machine, not one per user, because the server may well be running as
a service under an account nobody logs into. The COM shim reads the port out
of this same file, so there is one place that decides where the server is.

The file is written the first time you save something from the setup page. If
it is missing, the server starts on its defaults.

### Opening the setup page

Browse to:

```
http://localhost:11111/
```

11111 is the Alpaca standard port and the default here. In tray mode, "Open
setup page" on the tray menu does the same thing.

If you changed the port, use the one you set. The setup page is the only
configuration interface: there is no separate settings program.

### Running the server by hand

The executable is `C:\Program Files\OnStepX ASCOM\OnStepX.AlpacaServer.exe`
and takes these options:

```
  --console            Run in the foreground. This is the default.
  --tray               System tray icon. Windows only.
  --service            Run as a Windows service or a systemd unit.
  --simulate           Use a simulated OnStepX controller, with no hardware.
                       Intended for conformance checking and for testing.
  --port <n>           HTTP port. Defaults to 11111, the Alpaca standard.
  --settings <path>    Use an alternative settings file.
  --help               Show this help.
```

If the service is already running, stop it first (`sc stop "OnStepX ASCOM"`
from an elevated prompt). Two copies cannot share the port, and they certainly
cannot share the serial port.

`--simulate` is worth knowing about even if you have hardware: it starts a
fully working server backed by a simulated controller, so you can look around
the setup page and point a client at it with nothing plugged in. Nothing it
does is persisted, so a run with `--simulate` does not leave the driver in
simulated mode afterwards.

### Uninstalling

Use "OnStepX ASCOM" in Apps and features, or the "Uninstall OnStepX ASCOM"
Start Menu entry. It stops and removes the service, unregisters the COM shim,
and removes the installed files and shortcuts.

It leaves `C:\ProgramData\OnStepX ASCOM\settings.json` in place, so
reinstalling keeps your configuration. Delete that file by hand if you want to
start clean.

---

## Linux and Raspberry Pi

The typical arrangement is a Raspberry Pi at the telescope, cabled to the
controller, with the imaging computer elsewhere on the network. Nothing stops
you running it on a desktop Linux machine instead; the steps are the same.

### Download the right tarball

Two builds are published:

| Asset | For |
| --- | --- |
| `onstepx-alpaca-linux-x64.tar.gz` | 64 bit Intel or AMD |
| `onstepx-alpaca-linux-arm64.tar.gz` | 64 bit ARM, which includes the Raspberry Pi 3, 4, 5 and Zero 2 W running a 64 bit OS |

`uname -m` tells you which you need: `x86_64` means x64, `aarch64` means
arm64. If it reports `armv7l` you are running a 32 bit OS, and there is no
build for that. Reinstall the 64 bit version of Raspberry Pi OS.

Both are self contained. The .NET runtime is inside the tarball, so there is
no runtime to install.

### Unpack it

The tarball has no top level directory inside it, so create the destination
first rather than extracting into your home directory and finding fifty files
there:

```sh
sudo mkdir -p /opt/onstepx-alpaca
sudo tar -xzf onstepx-alpaca-linux-arm64.tar.gz -C /opt/onstepx-alpaca
```

`/opt/onstepx-alpaca` is the path the supplied systemd unit expects. If you
put it somewhere else, edit `WorkingDirectory` and `ExecStart` in the unit to
match.

### Create the account it runs as

The server does not need root, and should not have it. It needs exactly one
privilege beyond an ordinary account: permission to open the serial port,
which on every mainstream distribution means membership of the `dialout`
group.

```sh
sudo useradd --system --create-home --home-dir /var/lib/onstepx \
    --groups dialout onstepx
```

`--create-home` is not optional here. The server stores its settings under the
account's home directory (see below), and a service account with a home
directory that does not exist cannot save its configuration.

The unpacked files can stay owned by root: the service only reads them.

### Install the systemd unit

The unit is `packaging/linux/onstepx-alpaca.service` in the repository:

```sh
curl -fLO https://raw.githubusercontent.com/juanjol/onstepx_alpaca_open_driver/main/packaging/linux/onstepx-alpaca.service
sudo cp onstepx-alpaca.service /etc/systemd/system/
```

It is not inside the `v0.1.0-beta.1` tarballs, which were built before the
file existed in the repository. The build workflow copies it into the tarball
when it is present, so if you unpack a later release and find an
`onstepx-alpaca.service` next to the executable, use that copy rather than
downloading one.

### Enable and start it

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now onstepx-alpaca
systemctl status onstepx-alpaca
```

`enable --now` does both halves: start it now, and start it at every boot.

The unit is `Type=notify`, so systemd reports the service as active only once
the server has finished starting and is actually listening, not merely once
the process exists.

### Where the settings file ends up

The server follows the XDG convention, so the path depends on the account it
runs as. Under the unit above, running as `onstepx` with the home directory
created in the previous step, that is:

```
/var/lib/onstepx/.config/onstepx-ascom/settings.json
```

If `XDG_CONFIG_HOME` is set for the account, it goes to
`$XDG_CONFIG_HOME/onstepx-ascom/settings.json` instead.

Note that running the server by hand as yourself uses **your** home directory,
so it will not see the service's configuration. That is usually what you want
while testing, but it does explain why a setting you saved from a hand started
copy is missing when the service comes back.

### Opening the setup page

From the Pi itself:

```
http://localhost:11111/
```

From another machine on the network, use the Pi's address, for example
`http://192.168.1.50:11111/`. The server accepts connections from outside the
machine by default, so nothing needs enabling for this. If the Pi runs a
firewall, allow the TCP port, and allow the UDP Alpaca discovery traffic as
well if you want clients to find the server without typing its address.

### Checking the logs

The server writes to standard output and has no log file of its own, so under
systemd everything goes to the journal:

```sh
journalctl -u onstepx-alpaca -f          # follow it live
journalctl -u onstepx-alpaca -b          # this boot
journalctl -u onstepx-alpaca -n 200      # the last 200 lines
```

### Stopping, restarting, removing

```sh
sudo systemctl restart onstepx-alpaca
sudo systemctl stop onstepx-alpaca
sudo systemctl disable --now onstepx-alpaca
```

To remove it entirely, disable it, delete
`/etc/systemd/system/onstepx-alpaca.service`, run `sudo systemctl
daemon-reload`, and delete `/opt/onstepx-alpaca`. The settings file under
`/var/lib/onstepx` and the `onstepx` account survive that, so remove them
separately if you want nothing left.
