# Firmware Bug Principle

## Loading Loop

The main display loop compares the saved content version with `21010100`. When it matches, the firmware enters loading mode and does not have a normal exit path. Since this state may come from NAND content updates, power cycling can reload the same state and trigger the loop again.

The patch replaces the comparison value with a value that should not normally match.

## Overlapping Pages and Flicker

Slot 8 and slot 9 should be mutually exclusive, but two firmware paths enable slot 8 without first disabling slot 9. Under some page, rotation, or update-state transitions, both slots can remain enabled and two UI pipelines write to the same framebuffer, causing overlapping pages, flicker, or corrupted display.

The patch clears slot 9 enable before enabling slot 8.

