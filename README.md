# Rescue Whale

A small, open-source settings app for gaming mice built on the Compx chipset (USB vendor ID `3554`). Every setting is on one screen, and changes save to the mouse as you make them.

**Rescue Whale was built and tested for one mouse: the Attack Shark X8 Ultra.** Other Attack Shark models are not supported yet. If you own one, you can [help add it](#help-add-your-mouse).

![Rescue Whale](preview.png)

## What it does

- **DPI:** up to six stages from 50 to 42,000, each with an indicator color
- **Polling rate:** 125 Hz to 8,000 Hz
- **Buttons:** remap the six buttons to clicks, back/forward, DPI stepping, scrolling, polling-rate switching, or disabled
- **Sensor:** lift-off distance, sensor mode, motion sync, angle snapping, ripple control
- **Power:** sleep timer and click response (debounce) time
- **Status:** connection, battery level, model, device ID, and firmware version

It finds the mouse on its own, over the USB cable or the wireless receiver, and reconnects when the mouse wakes up.

## What it does not do

- Macros, key combinations, media keys, and rapid-fire. Any already on the mouse show as `CUSTOM` and are left untouched.
- Lighting effects and the receiver's LED
- Profiles
- Pairing and firmware updates. Use the manufacturer's software for those.

## Tested hardware

Every feature listed above has been tested by the author on the Attack Shark X8 Ultra, over both of its connections:

| Connection | USB ID | Status |
| --- | --- | --- |
| USB cable | `3554:FB43` | Works: settings read and save. |
| 8K wireless receiver | `3554:FB44` | Works: settings read and save. |

Nothing else has been tested. The DPI range, the number of buttons, and the polling rates are currently fixed to the X8 Ultra's values, so another model may show options it doesn't have or miss ones it does.

## Help add your mouse

I only own an X8 Ultra, so supporting other Attack Shark models depends on people who own them. If you have one, please open an issue using the **New mouse report** template with the following.

**What the app shows.** Run Rescue Whale with the mouse connected and click the status box in the top-right corner. That copies its five lines, ready to paste into the issue. The `DEVICE ID` line is the important part: it holds the USB ID and the mouse's own model ID.

**What happened.** One of:

- "No mouse found", so the app didn't recognize the device at all
- Connected but read-only, so the mouse stores its settings in a layout the app doesn't know
- Connected and editable, in which case please say which settings worked and which didn't

**About the mouse.**

- Exact model name, and the sensor if you know it
- Lowest and highest DPI, and the DPI step, as shown in the manufacturer's software
- Number of programmable buttons, and which is which
- Polling rates offered, wired and wireless
- Whether it has a DPI indicator light or other lighting
- Both connections if you can: the app's status box once on the cable and once on the wireless receiver

**Optional but very helpful.** A screenshot of each page of the manufacturer's software for that mouse, so the settings and their allowed values are clear.

Please try a model the app shows as read-only before anything else. In that state Rescue Whale writes nothing to the mouse, so reporting it is risk-free. If you can write C#, new models are added in `Mouse.cs` (see `KnownModels`), and pull requests are welcome.

## Download and run

Download `RescueWhale.exe` from the Releases page and run it. There is nothing to install.

- `RescueWhale.exe` runs on any 64-bit Windows 10 or 11 PC.
- `RescueWhale-small.exe` is much smaller but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Close the manufacturer's software before using Rescue Whale, so the two don't write to the mouse at the same time.

To look around without a mouse connected, start it in demo mode:

```
RescueWhale.exe --demo
```

## Disclosures

- **Built and tested for the Attack Shark X8 Ultra only.** It has not been run on any other mouse.
- **Unofficial.** Rescue Whale is an independent project. It is not affiliated with, endorsed by, or supported by Attack Shark or Compx. Product names are trademarks of their owners and are used only to say what the app works with.
- **Use at your own risk.** The app writes settings to your mouse's memory, and it is provided with no warranty (see the [license](LICENSE)). Before writing, it checks that the settings it read carry the checksums it expects, and goes read-only if they don't. That is a safeguard, not a guarantee.
- **Not a full replacement.** See [What it does not do](#what-it-does-not-do). Keep the manufacturer's software for those jobs.
- **How the protocol was worked out.** By querying my own mouse and reading the configuration files installed with the manufacturer's software. No manufacturer code was copied or decompiled, and none of their files are included here.
- **AI-assisted.** The code, icon, and documentation were written with Claude, Anthropic's AI, under my direction, and tested by me on my own hardware.
- **Unsigned program.** The download is not code-signed, so Windows shows a "Windows protected your PC" warning on first run. Choose **More info**, then **Run anyway**.
- **Privacy.** No telemetry, no network connections, and nothing stored on your PC. Your settings live only on the mouse.
- **Early software.** This is version 0.1.0 by a single author. Expect rough edges, and please report what you find.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```
dotnet build
dotnet run
```

To produce the single-file release build:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

## How it talks to the mouse

The mouse exposes a vendor-defined HID collection (usage page `FF02`) that exchanges 17-byte reports. Each report carries a command, an address, a length, up to ten bytes of data, and a checksum. Settings live in a small block of memory on the mouse, where every value is stored alongside its own check byte.

- `Hid.cs` finds the device and sends and receives reports.
- `Mouse.cs` maps that memory to named settings.
- `MainWindow.xaml` and `MainWindow.xaml.cs` are the interface.

## License

[MIT](LICENSE)
