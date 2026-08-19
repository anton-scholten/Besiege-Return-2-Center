# Changelog

## 0.2.0

Everything below is on top of 0.1.3, the last released version. Machines built
with that version load and behave as they did, apart from the fixes.

**Added**

- A **Tension** slider on both blocks, the one the built-in hinge and steering
  block have and these did not. Same range, same logarithmic handle, and the same
  sixth-power curve, so it stiffens and softens the joint by the same amounts.

**Fixed**

- The block shown beside the limit dials was wrong on both blocks. The hinge was
  drawn end-on, so it read as a flat plate rather than a hinge; the steering
  block was oversized and off centre. Both now use the same position, rotation
  and scale the built-in blocks use.
- Nothing was reset between simulation runs. From the second run on, the block
  kept the limits, flip and mode it had picked up on the first one — changing the
  mode in the mapper did nothing until the machine was reloaded — and it snapped
  back to the angle it had stopped at.
- The blocks ignored emulated keys, so nothing but a player could steer them.
  They now read emulation the way the game's own steering hinge does.

**Changed**

- Rebuilt against current Besiege. `AddLimits` had gained a parameter since 2018,
  which is what stopped the mod compiling at all.
- The assembly is `Return2CenterAssembly.dll` rather than `C#.dll`.

The source was recovered from the shipped assembly — the original was lost. See
[docs/RECOVERY.md](docs/RECOVERY.md).

## 0.1.3

The last released Workshop version, built in 2018.
