# SimHub.Plugin.Voicemeeter

A [SimHub](https://www.simhubdash.com/) plugin that lets you control
[Voicemeeter](https://vb-audio.com/Voicemeeter/) (Basic, Banana or Potato)
directly from SimHub: gain and mute for every input strip and output bus,
plus a live settings screen showing the current values.

Talks to Voicemeeter via the official Voicemeeter Remote API
(`VoicemeeterRemote.dll`, the 32-bit build - SimHub itself runs as a 32-bit
process), so Voicemeeter itself must be installed on the same machine. The
plugin auto-detects which edition is running and exposes the channel count
for that edition (3 strips / 2 buses for Basic, 5/5 for Banana, 8/8 for
Potato).

## Features

- Per-strip and per-bus gain (-60 dB to +12 dB) and mute, both as live
  read-only SimHub properties (`Voicemeeter.StripN.Gain`,
  `Voicemeeter.StripN.Mute`, `Voicemeeter.BusN.Gain`, `Voicemeeter.BusN.Mute`,
  plus `Label` for each) usable in dashboards and formulas.
- **User-defined presets**: create a preset from a channel (any strip/bus),
  a function (set gain, step gain, mute on, mute off, toggle mute) and a
  value, from the settings screen. Each preset registers exactly one SimHub
  action, so the number of Controls-and-Events targets this plugin exposes
  is only however many presets you actually create - not every possible
  channel/function/value combination. A "Test fire" button per preset lets
  you try it immediately without needing a restart.
- Settings screen with a live-updating gain slider and mute checkbox per
  channel (for monitoring/manual control), plus the preset list.
- Automatically reconnects if Voicemeeter isn't running yet or is restarted.

## Project layout

```
SimHub.Plugin.Voicemeeter.slnx          <- solution (this folder)
ReadMe.md                                <- this file
SimHub.Plugin.Voicemeeter/               <- plugin project
    SimHub.Plugin.Voicemeeter.csproj
    VoicemeeterPlugin.cs                 <- IPlugin/IDataPlugin/IWPFSettingsV2 entry point
    VoicemeeterRemote.cs                 <- P/Invoke wrapper for VoicemeeterRemote.dll
    VoicemeeterPluginSettings.cs
    VoicemeeterType.cs
    ChannelPreset.cs                     <- a single user-defined preset (channel + function + value)
    ChannelRowViewModel.cs               <- live binding source for the settings grid rows
    SettingsControl.xaml(.cs)            <- settings screen UI
```

## Building

Requires Visual Studio (or `msbuild`) with the .NET Framework 4.8 targeting
pack, and a local SimHub install (the project references SimHub's own
assemblies via the `SIMHUB_INSTALL_PATH` environment variable, which the
SimHub installer sets automatically - normally
`C:\Program Files (x86)\SimHub\`).

Open `SimHub.Plugin.Voicemeeter.slnx` and build. The post-build step copies
the compiled plugin DLL/PDB straight into your SimHub install folder; restart
SimHub afterwards to load it.

## Requirements

- SimHub (desktop).
- Voicemeeter Basic, Banana or Potato installed and running.
- .NET Framework 4.8.

## Notes

- Gain values are in dB and match Voicemeeter's own -60..+12 range.
- Adding or removing a preset takes effect immediately for "Test fire" on the
  settings screen, but SimHub only reads a plugin's action list once at
  startup - a preset only appears (or disappears) as a target in Controls
  and Events after restarting SimHub.
- If Voicemeeter isn't running when SimHub starts, the plugin keeps retrying
  the connection in the background and picks it up automatically once it is.
- Channel indexes are 0-based and follow Voicemeeter's own strip/bus order
  (hardware inputs/outputs first, then virtual inputs/outputs).
