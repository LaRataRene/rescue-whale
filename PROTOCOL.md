# Attack Shark X8 Ultra protocol notes

How Rescue Whale talks to the Attack Shark X8 Ultra, written for anyone adding X8 Ultra support to their own software. Everything here was confirmed on one mouse, with mouse firmware 3.00 and receiver firmware 2.02. Other firmware versions and other models may differ.

All numbers are hexadecimal unless they are obviously decimal (DPI values, percentages, Hz).

## Devices

| Connection | USB vendor | USB product |
| --- | --- | --- |
| USB cable | `3554` | `FB43` |
| 8K wireless receiver | `3554` | `FB44` |

Use the vendor-defined HID collection with usage page `FF02`. It has 17-byte input and output reports, with report ID `08`.

## Packet format

Requests and replies have the same 17-byte layout:

| Byte | Meaning |
| --- | --- |
| 0 | Report ID, always `08` |
| 1 | Command |
| 2 | `00` |
| 3 | Address, high byte |
| 4 | Address, low byte |
| 5 | Length of the data that follows |
| 6–15 | Data, up to ten bytes, zero-padded |
| 16 | Checksum |

The checksum is `55` minus the sum of bytes 0 to 15, keeping the low eight bits.

A reply repeats the command and address of the request. Match replies to requests on both.

## Commands

| Command | Purpose | Reply data |
| --- | --- | --- |
| `03` | Is the mouse online? | Byte 0 is `01` when online |
| `04` | Battery | Byte 0 is the level in percent; byte 1 is `01` while charging |
| `07` | Write settings memory | Echoes the request |
| `08` | Read settings memory | The bytes read |
| `10` | Model ID | Byte 0 is the customer ID, byte 1 the model ID. The X8 Ultra reports `7C 01` |
| `12` | Mouse firmware version | Bytes 0 and 1, shown as `3.00` for `03 00` |
| `1D` | Receiver firmware version | Bytes 0 and 1, shown as `2.02` for `02 02` |
| `14` | Set the receiver's light | Echoes the request |
| `15` | Get the receiver's light | Ten bytes, see below |

Reads and writes take an address in bytes 3–4 and a length in byte 5. Ten bytes is the most one packet carries, so read the settings memory in ten-byte steps.

## Settings memory

Settings live in a small block of memory on the mouse, addresses `00` to `BF`. Every value carries its own check:

- **Single values** are stored as two bytes, the value and a check byte, which add up to `55`.
- **Four-byte entries** (DPI values, stage colors, buttons) are three data bytes and a check byte, and all four add up to `55`.

Check these sums after reading, before trusting the layout or writing anything back. When writing, send the check byte along with the value.

| Address | Setting | Format |
| --- | --- | --- |
| `00` | Polling rate | Single value, see codes below |
| `02` | Number of DPI stages | Single value |
| `04` | Active DPI stage | Single value, counted from 0 |
| `0A` | Lift-off distance | Single value: `01` is 1 mm, `02` is 2 mm, `03` is 0.7 mm |
| `0C` | DPI stage values | Four bytes per stage, see below |
| `2C` | DPI stage colors | Four bytes per stage: red, green, blue, check |
| `60` | Button functions | Four bytes per button: type, parameter 1, parameter 2, check |
| `A9` | Click response (debounce) | Single value, in milliseconds |
| `AB` | Motion sync | Single value, `00` or `01` |
| `AD` | Sleep timer | Single value, in units of 10 seconds |
| `AF` | Angle snapping | Single value, `00` or `01` |
| `B1` | Ripple control | Single value, `00` or `01` |
| `B9` | Sensor mode | Single value: `00` low power, `01` high performance |

### Polling rate codes

| Rate | Code |
| --- | --- |
| 125 Hz | `08` |
| 250 Hz | `04` |
| 500 Hz | `02` |
| 1,000 Hz | `01` |
| 2,000 Hz | `10` |
| 4,000 Hz | `20` |
| 8,000 Hz | `40` |

### DPI values

Each stage is four bytes at `0C` + 4 × stage:

| Byte | Meaning |
| --- | --- |
| 0 | Low eight bits of the value (X axis) |
| 1 | Low eight bits of the value (Y axis), the same as byte 0 |
| 2 | High bits and step flag |
| 3 | Check byte |

The value is the DPI divided by the step, minus 1:

- From 50 to 26,000 DPI the step is 50.
- From 26,100 to 42,000 DPI the step is 100, and the flag bits `11` are set in byte 2.

Byte 2 holds the two bits above the low eight, once for each axis: shifted left by 2 for X and by 6 for Y, combined with the flag bits when the step is 100.

Examples:

| DPI | Bytes |
| --- | --- |
| 1,600 | `1F 1F 00 17` |
| 8,000 | `9F 9F 00 17` |
| 42,000 | `A3 A3 55 BA` |

### Button functions

Six buttons, in this order: left, right, wheel click, side rear, side front, DPI button. Each is four bytes at `60` + 4 × button.

| Function | Type | Parameter 1 | Parameter 2 |
| --- | --- | --- | --- |
| Disabled | `00` | `00` | `00` |
| Left click | `01` | `01` | `00` |
| Right click | `01` | `02` | `00` |
| Middle click | `01` | `04` | `00` |
| Back | `01` | `08` | `00` |
| Forward | `01` | `10` | `00` |
| DPI: next stage, looping | `02` | `01` | `00` |
| DPI: stage up | `02` | `02` | `00` |
| DPI: stage down | `02` | `03` | `00` |
| Scroll left | `03` | `01` | `00` |
| Scroll right | `03` | `02` | `00` |
| Scroll up | `0B` | `01` | `00` |
| Scroll down | `0B` | `02` | `00` |
| Polling rate: next | `07` | `00` | `00` |

Other types exist for key combinations, media keys, rapid-fire and macros. Rescue Whale does not write those and leaves any it finds untouched.

### Sleep timer values

The value is the time in units of 10 seconds. The manufacturer's software offers `01` (10 s), `03` (30 s), `06` (1 min), `0C` (2 min), `12` (3 min), `1E` (5 min), `3C` (10 min) and `5A` (15 min).

## Receiver light

The receiver's light setting lives on the receiver, not the mouse. Send these commands to the receiver device (`FB44`), with address `0000`.

- Command `15` returns ten bytes.
- Command `14` writes ten bytes back.

Byte 0 is the mode:

| Mode | Light shows |
| --- | --- |
| `00` | Off |
| `01` | Polling rate |
| `02` | Battery level |
| `03` | Low-battery warning only |

To change the mode, read the ten bytes, change byte 0, and write all ten back.

## Where this is in the code

- [`Hid.cs`](Hid.cs) finds the device, builds packets, and matches replies.
- [`Mouse.cs`](Mouse.cs) holds the memory map, the check-byte rules, and the DPI encoding.
- [`MainWindow.xaml.cs`](MainWindow.xaml.cs) has the receiver light commands.

Rescue Whale is MIT licensed, so this code can be reused.
