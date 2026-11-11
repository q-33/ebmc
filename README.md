EBMC
====

EBMC is the East Bay Makers Club's edition of the open-source 3D printing program MatterControl. It prepares,
slices and prints parts on the club's printer (a Pulse XE S-232 with a Mosquito hotend), with the club's
printer profile, theme and badge built in and the vendor's online services removed.

It is meant for members of the East Bay Makers Club (https://eastbaymakersclub.com) who print on that
printer. Questions go to the club, not to MatterHackers, who do not support this build.

Building
--------

* Mac (Apple silicon or Intel) and Linux: see `MACOS.md`. In short, with the .NET 10 SDK and the submodules
  checked out (`git submodule update --init --recursive`), `Tools/macOS/build-app.sh` builds `dist/EBMC.app`,
  and `dotnet build MatterControl.CrossPlatform -c Release` builds the application for development.
* What this edition changes from upstream, and how its generated files are made: see `EBMC.md`.

Credits and licences
--------------------

See `NOTICE.md` for the credits and for the licences the application is distributed under, and `LICENSE`
for the MatterControl licence. The licence texts of the bundled components are in `StaticData/License` and
are shown in the app under About.

Based on MatterControl by MatterHackers, Inc. — upstream: https://github.com/MatterHackers/MatterControl
