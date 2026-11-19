EBMC
====

Version 1.0

EBMC is the East Bay Makers Club's edition of the open-source 3D printing program MatterControl. It prepares,
slices and prints parts on the club's printer (a Pulse XE S-232 with a Mosquito hotend), with the club's
printer profile, theme and badge built in and the vendor's online services removed.

It is meant for members of the East Bay Makers Club (https://eastbaymakersclub.com) who print on that
printer. Questions go to the club, not to MatterHackers, who do not support this build.

What it adds
------------

* The club printer set up out of the box: profile, 0.3 mm nozzle, material presets, and the club's dark theme.
  It is the only printer offered at setup.
* Simple and Advanced modes. Simple, where a new user starts, shows fewer tools and settings; Advanced shows
  everything.
* Club shapes and starters: rounded boxes, slot plates, tubes, gears and fill-in designs such as a name tag or
  a sign, plus exact solids with true rounded edges and clean holes.
* Find a tool (Cmd/Ctrl + K): search every tool, shape and starter by the words Fusion and Tinkercad use.
* Printing through the club's OctoPrint server: the printer bar shows Send, and a "Ready to print?" check
  names the material and temperatures before a job starts.
* Export for laser: the 2D outline of a design as SVG or DXF.
* Members: a list of club members kept on this computer only, encrypted with its own passphrase.
* No vendor services: no store, account sign-in, update check or upsell.

`EBMC.md` has the full list, the files behind each change, the printer profile values and the notes on
printing through OctoPrint.

Status
------

Version 1.0 is built and used on an Apple silicon Mac. The Linux build was tested end to end on a virtual
display, not on a desk. The Windows application still compiles but has not been run in this edition. The
printer runs Smoothieware firmware; the profile and the notes in `EBMC.md` are written for that printer and
no other.

Building
--------

* Mac (Apple silicon or Intel) and Linux: see `MACOS.md`. In short, with the .NET 10 SDK and the submodules
  checked out (`git submodule update --init --recursive`), `Tools/macOS/build-app.sh` builds `dist/EBMC.app`,
  and `dotnet build MatterControl.CrossPlatform -c Release` builds the application for development.
* What this edition changes from upstream, and how its generated files are made: see `EBMC.md`.

Testing
-------

`dotnet test Tests/EBMC.Tests` runs the edition's own tests (about 1,400; no window is opened and no printer
or network is used). MatterControl's older test projects under `Tests/` are Windows-only and are not run here.

`Tools/macOS/ui-smoke.sh all` is the on-screen smoke test for the Mac: it starts the real application on an
empty scratch data folder, clicks through Members, restore from backup, the Simple/Advanced switch, slicing
(nothing is sent anywhere) and laser export by widget name, saves a screenshot of every step under
`out/ui-smoke/`, and fails on any error. It needs no screen-recording or accessibility permission. See
`MACOS.md`.

In this repository
------------------

* `EBMC.md`: what this edition changes and why. `MACOS.md`: building and running on a Mac or Linux.
* `Tools/EBMC/`: the scripts that generate the club profile, themes and badge artwork. Change a value there
  and re-run the script; do not edit the generated files by hand.
* `Tools/macOS/build-app.sh`: builds `dist/EBMC.app`.
* `Tools/octoprint-ebmc-stop/`: a small OctoPrint plugin that adds a "STOP PRINT and HOME ALL AXES" button
  under OctoPrint's temperature graph. Tested on a local OctoPrint only; see its own README before installing.
* `tasks/todo.md`: the working notes and plans.

Credits and licences
--------------------

See `NOTICE.md` for the credits and for the licences the application is distributed under, and `LICENSE`
for the MatterControl licence. The licence texts of the bundled components are in `StaticData/License` and
are shown in the app under About.

Based on MatterControl by MatterHackers, Inc. — upstream: https://github.com/MatterHackers/MatterControl
