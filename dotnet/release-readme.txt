OpenVersus
==========

A free, open-source mod that lets you keep playing MultiVersus online. If you paid anyone for
these files, ask them for a refund.


Installing
----------

Extract everything in this zip into the folder that holds "MultiVersus-Win64-Shipping.exe".
For a default Steam install that is:

    C:\Program Files (x86)\Steam\steamapps\common\MultiVersus\MultiVersus\Binaries\Win64

The easy way to find it: right-click MultiVersus in your Steam library, choose Manage, then
Browse local files, and open the folders "MultiVersus", "Binaries", "Win64". Extract the zip
there, so that xinput1_3.dll sits next to MultiVersus-Win64-Shipping.exe and the plugins
folder is beside them.

For Steam Deck users, the xinput1_3.dll proxy DLL file may need to be renamed to version.dll
instead of xinput1_3.dll. Some Steam Deck users have reported controller or input issues while
using the proxy DLL when its name is xinput1_3.dll, and renaming it to version.dll has been a
common workaround.


You'll know you installed it correctly if you see an "OpenVersus Loaded" notification after
launching the game and clicking past the title screen. During the first launch, you will also
get a one-time notice that OpenVersus is a free mod.


Upgrading from an older version of OpenVersus
----------------------------------------------

Extract the new zip over the old install. If an older copy of the mod is still there, the new
one finds it, renames it to .bak so it no longer loads, and then closes the game; launch
MVS again and you're done. Any old OpenVersus.ini settings files are converted to the modern
OpenVersus.toml format automatically, and if the old file can't be converted, you'll start
from default settings and the game will tell you that this happened.

Updates
-------

The mod checks for a new version each time the game starts. When a new version is available, the
update is downloaded, checked against that release's checksum to verify its integrity and that
it has not been tampered with, installed, and then the game is closed; just launch it again and
you're done. To turn this update feature off, set AutoUpdate = false in plugins\OpenVersus\OpenVersus.toml.
But you probably shouldn't do that, since you need the latest update to play on the official
OpenVersus servers.


Settings
--------

The plugins\OpenVersus\OpenVersus.toml text file holds the mod's configurable settings, with a comment explaining
each one. Edit it with the game closed. If you happen to break it, the mod will run with its default
built-in settings on the next launch; it'll also tell you exactly where in the config file to look to
fix the problem. You may also just delete the broken config, and a fresh config file will be written
out to disk the next time that you launch the game.
plugins\OpenVersus\sample_config.toml is a copy of the defaults for reference; the mod never
reads it.


Reporting a problem
-------------------

Include plugins\OpenVersus\logs\OpenVersus.log from the session where it happened. Older sessions are kept
beside it, named for when they started. If it's a network problem, open the plugins\OpenVersus\OpenVersus.toml
file in a text editor and change the NetStats setting to true, reproduce the problem, and then
include any files whose name begin with "NetStats" in the "logs" directory as well. You should
probably disable the NetStats feature during normal, regular play. It's not an expensive feature
resource-wise, but it does collect per-frame stats, and it writes those stats out to its own log
file every second, so there is a chance it could cause performance issues on lower-end computers
if you have it enabled all the time.


Uninstalling
------------

Delete xinput1_3.dll (or version.dll, if you renamed it), and the OpenVersus folder inside of the
plugins folder next to xinput1_3.dll/version.dll. That's it. You're done.


Links
-----

The mod:     https://github.com/openversus/ovs-client
Everything:  https://github.com/openversus

OpenVersus is not affiliated with, nor endorsed by, WB, PFG, its developers, or any other related
entity, nor by any other modding project or effort. It is provided as-is, without warranty, as
detailed in plugins\OpenVersus\LICENSE.txt. Use at your own risk and discretion.
