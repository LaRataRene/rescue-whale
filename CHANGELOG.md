# Changelog

All notable changes to Rescue Whale are listed here, newest first.

## Unreleased

### Added

- **Battery gauge.** A battery drawn in text characters, with a vertical level and a large percentage, now sits next to the status box. It is green from 50%, amber below that, and red under 20%, and says when the mouse is charging.
- **Battery history.** Clicking the gauge or the percentage opens a graph of the battery level over the last 24 hours, 7 days, or 30 days. Time on battery and time charging are drawn in different colors, and stretches with no readings are dashed.

- **Resizable down to half size.** The window can now be made much smaller; below its normal size the whole interface shrinks to fit instead of being cut off.
- `--size WIDTHxHEIGHT` opens the window at a given size.

### Changed

- **No Windows title bar.** The minimize, maximize and close buttons are now part of the interface, in a thin strip at the top that also shows the version. Drag the strip, the logo, or the title to move the window; double-click to maximize.
- The battery line is gone from the status box, since the gauge replaces it. Clicking the box now copies four lines.
- Rescue Whale now keeps a second file on your PC: battery readings for the history graph, in `%AppData%\RescueWhale\battery.csv`, trimmed to the last 45 days.

### Known limits

- Battery history is only recorded while Rescue Whale is open and the mouse is awake. Anything else shows as a gap, and the history starts from the first time this version runs.

## 0.2.0 - November 12, 2026

### Added

- **Receiver light control.** A new `[ RECEIVER LED ]` section sets what the wireless receiver's light shows: off, the polling rate, the battery level, or a low-battery warning only. The setting lives on the receiver, so it works while the mouse is asleep or on its cable.
- **Tips on every section.** Each section title has an `[i]` button that opens an explanation of its settings, including which receiver color means which polling rate.
- **Themes.** A `THEME` dropdown at the bottom right offers eight color schemes: the original phosphor green, five pure-black themes for OLED screens (green, amber, ice, mono, red), amber, and a light paper theme. The choice is remembered between launches.
- `--theme NAME` starts the app in a theme for one run without saving it.

### Changed

- The always-visible hint lines under each section are gone; their content moved into the `[i]` tips, which makes the window more compact.
- The app now keeps a connection to the wireless receiver even when the mouse is on its cable, so the receiver's light can be set either way.
- Rescue Whale now stores one thing on your PC: your theme choice, in `%AppData%\RescueWhale\theme.txt`. Version 0.1.0 stored nothing. There is still no telemetry and no network use.

### Confirmed working

- **DPI stage colors on the mouse.** The light on the mouse shows each stage's chosen color when you switch DPI stages. The `LED` color picker in `[ DPI STAGES ]` sets those colors.

### Known limits

- The colors of the receiver's light can't be changed, and it can't follow the DPI stage. On the tested receiver (firmware 2.02) the light kept its built-in colors whatever was stored, and the DPI mode stayed dark, so neither is offered.
- Receiver light control has been tested on the 8K receiver (`3554:FB44`) only. Other receivers are read-only unless the app is started with `--unlock`.

## 0.1.0 - November 11, 2026

First release, built and tested for the Attack Shark X8 Ultra.

- DPI stages, polling rate, button remapping, sensor options, sleep timer, and click response time, on one screen.
- Works over the USB cable and the 8K wireless receiver, and finds the mouse on whichever one it answers.
- Status box with connection, battery, model, device ID, and firmware; click it to copy the details.
- Untested mouse models are read-only unless the app is started with `--unlock`.
- `--demo` runs the app with sample data and no mouse.
