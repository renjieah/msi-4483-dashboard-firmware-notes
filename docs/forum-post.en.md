# MSI 4483 Dashboard Panel Firmware Long-Run Bug Notes

Device: MSI 4483 Dashboard panel, USB HID device `VID=0x0DB0 / PID=0x4BB6`  
Firmware: `conprog_4BB6_22100300.bin`

## Symptoms

I ran into two issues that only show up after the panel has been running for a while:

1. After a few days, the panel gets stuck on the `loading` screen. Rebooting does not recover it; reflashing the firmware/NAND is required.
2. After some runtime, two UI pages may overlap, flicker, or corrupt the display. In some cases, rebooting does not clear the problem.

## What Seems To Cause It

These do not look like pure hardware failures. They look more like firmware state-machine and page-switching bugs.

The first issue is the `loading` loop.

Inside the firmware main loop, there is a check that compares the saved content version against `21010100`. This looks like some kind of recovery/update condition, but the actual behavior is bad: once the value becomes `21010100`, the firmware jumps into loading mode and does not appear to have a normal way out.

The annoying part is that MSI Center or the update tool may write a content package with that version into the panel NAND. Once the firmware reads it, the panel enters the loading loop. Since the state is already stored in NAND/config data, a simple power cycle does not help; the panel boots, reads the same state again, and gets stuck again.

The second issue is the overlapping/flickering UI.

The panel firmware has multiple display page slots. You can think of them as separate page states. Under normal conditions, slot 8 and slot 9 should be mutually exclusive: before one is enabled, the other should be disabled. In the code I checked, there are two paths where the firmware enables slot 8 without first disabling slot 9.

After some update or rotation-state change, this can lead to:

1. Slot 9 is still enabled.
2. The firmware enables slot 8.
3. Both slots think they should be displayed.
4. Two UI pipelines write to the same framebuffer.

The visible result is overlapping pages, flicker, or a corrupted display. If this bad state is written back into NAND, rebooting may restore the same broken state, which explains why a reboot may not fix it.

## Current Fix Idea

I made a small patch that changes only 3 locations:

1. Replace the comparison value that triggers the loading loop with a value that should not normally match.
2. Before enabling slot 8, explicitly disable slot 9.
3. Apply the same slot 9 clear in the other matching slot 8 path.

This is a tiny firmware patch, not a full rewrite. I will put the patch script and notes on GitHub so others can review them.

## Warning

Flashing firmware is risky. A bad flash may make the device fail to boot. This note is mainly for people who have the same issue and understand the recovery risk. I do not recommend flashing without a recovery plan.

## One More Thing

Besides fixing the bugs, I also made an experimental custom display path. The idea is to make this panel do more than the stock MSI pages. In short, it turns the panel from a fixed MSI status screen into a 480x800 custom secondary display driven by a PC app.

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

The current UI is basically a custom hardware-monitoring dashboard. The main screen shows key status, and touch can switch into detail pages. Since the UI is WebView2-based, changing the theme, layout, charts, or pages is mostly just editing web files.

One nice thing now is that AI makes this kind of UI modification much easier. The UI is plain HTML/CSS/JS, not something locked inside the firmware. Even after the app is packaged, the web assets can remain exposed, so you can directly change layout, colors, pages, or widgets. Even if you do not write frontend code, you can describe what you want to an AI tool, generate a modified version, and try it.

One important note: when running this app, it is best to stop MSI Dashboard / MSI Center services or background processes related to the panel first. The stock MSI software can access the same USB HID device. If both apps fight for the device at the same time, you may see stutter, flicker, occasional jumps back to stock pages, or unstable connection.

The current stable baseline is the v72 firmware, which has completed an 8-hour 5fps hardware run. v73 is a later candidate and has passed offline checks, but it has not been fully hardware-tested yet. So this is no longer just a "blink once" demo, but it is still an experimental project, not an official stable feature.

This part is mainly for tinkering and research. It is not required for normal users. I will put the source code, web UI, patch script, and notes on GitHub for anyone interested.
