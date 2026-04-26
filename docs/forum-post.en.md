# MSI 4483 Dashboard Firmware Bugs: Still Not Fixed From Z790 To Z790 MAX, So I Tried Fixing It Myself

I have been using the MSI 4483 Dashboard panel for a while, and I was honestly surprised that these issues still seem to exist across generations. From the Z790 boards to the newer Z790 MAX series, the small dashboard screen still has long-running firmware problems that can make it get stuck, flicker, or show corrupted pages.

After waiting for a proper fix for a long time, I ended up trying to analyze and patch the firmware myself.

Device:

- MSI 4483 Dashboard panel
- USB HID device: `VID=0x0DB0 / PID=0x4BB6`
- Firmware image: `conprog_4BB6_22100300.bin`

## The Problems

I mainly ran into two issues.

First, after the panel has been running for a few days, it can get stuck on the `loading` screen. Rebooting does not recover it. In my case, the only reliable way to recover was reflashing the firmware/NAND.

Second, after some runtime, two UI pages may overlap, flicker, or corrupt the display. Sometimes the broken state survives a reboot, so it does not look like a simple temporary UI glitch.

## What I Found

These do not look like pure hardware failures. They look more like firmware state-machine and page-switching bugs.

### Loading Loop

Inside the firmware main loop, there is a check that compares the saved content version against `21010100`.

That looks like some kind of recovery or update condition, but the actual behavior is bad: once the value becomes `21010100`, the firmware jumps into loading mode and does not appear to have a normal way out.

The annoying part is that MSI Center or the update tool may write a content package with that version into the panel NAND. Once the firmware reads it, the panel enters the loading loop. Since the state is already stored in NAND/config data, a simple power cycle does not help. The panel boots, reads the same state again, and gets stuck again.

### Overlapping / Flickering Pages

The panel firmware has multiple display page slots. You can think of them as separate page states.

Under normal conditions, slot 8 and slot 9 should be mutually exclusive: before one is enabled, the other should be disabled. In the code I checked, there are two paths where the firmware enables slot 8 without first disabling slot 9.

After some update or rotation-state change, this can lead to:

1. Slot 9 is still enabled.
2. The firmware enables slot 8.
3. Both slots think they should be displayed.
4. Two UI pipelines write to the same framebuffer.

The visible result is overlapping pages, flicker, or a corrupted display. If this bad state is written back into NAND, rebooting may restore the same broken state, which explains why a reboot may not fix it.

## My Patch

I made a small firmware patch that changes only 3 locations:

1. Replace the comparison value that triggers the loading loop with a value that should not normally match.
2. Before enabling slot 8, explicitly disable slot 9.
3. Apply the same slot 9 clear in the other matching slot 8 path.

This is a tiny patch, not a full firmware rewrite. The patch script validates the original bytes before writing changes, so it should stop if the firmware image does not match the expected version.

I also built a `bugfix-only` firmware image that only contains these fixes and does not include the custom display modifications.

## One More Thing: Custom Display Runtime

While working on this, I also experimented with turning the MSI dashboard panel into a custom 480x800 secondary display.

The idea is simple: instead of being limited to the stock MSI pages, the PC renders a custom UI and streams the result to the panel.

There are two parts:

1. Modified firmware: based on the original firmware, with a custom USB HID data path added so the PC can write frame data directly into the panel display buffer.
2. Companion app: `PanelRuntime` on Windows, built with .NET 8 + WebView2. It renders a 480x800 web UI, reads AIDA64/system information, and streams the rendered frame to the panel.

The app can currently do things like:

1. Show a custom dashboard page instead of being limited to stock MSI pages.
2. Use HTML/CSS/JS for the UI, so whatever the web page renders is what the panel displays.
3. Read AIDA64 sensor data, such as CPU/GPU temperature, load, fan speed, and power.
4. Read Windows network and storage status, and show pages for network, storage, temperature, and other details.
5. Use panel touch input to switch pages, such as home, CPU, cooling, network, and storage detail pages.
6. Run at low FPS when idle, usually 1fps, and increase to 5fps during interaction to reduce device stress.
7. Bypass the stock MSI Center page system; the PC renders the UI and streams the result in real time.

The current UI is basically a custom hardware-monitoring dashboard. Since the UI is WebView2-based, changing the theme, layout, charts, or pages is mostly just editing web files.

One nice thing now is that AI makes this kind of UI modification much easier. The UI is plain HTML/CSS/JS, not something locked inside the firmware. Even after the app is packaged, the web assets can remain exposed, so you can directly change layout, colors, pages, or widgets. Even if you do not write frontend code, you can describe what you want to an AI tool, generate a modified version, and try it.

One important note: when running this app, it is best to stop MSI Dashboard / MSI Center services or background processes related to the panel first. The stock MSI software can access the same USB HID device. If both apps fight for the device at the same time, you may see stutter, flicker, occasional jumps back to stock pages, or unstable connection.

The current stable custom-display baseline is the v72 firmware, which has completed an 8-hour 5fps hardware run. v73 is a later candidate and has passed offline checks, but it has not been fully hardware-tested yet.

## Warning

Flashing firmware is risky. A bad flash may make the device fail to boot. This is not an official MSI fix, and I do not recommend flashing anything unless you understand the risk and have a recovery plan.

## GitHub

I put the notes, patch script, source code, screenshots, firmware hashes, release binaries, and the packaged Windows app here:

https://github.com/renjieah/msi-4483-dashboard-firmware-notes

