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
- Tapping a steering key that had been rebound to a variable still registered as
  a key release, which stopped an S2S sweep. `MKey.IsReleased` is the one key
  property that does not check for a variable binding.
- The blocks ignored **variables**, and emulated keys generally, so nothing but a
  player at the keyboard could steer them. A key bound to a variable reports
  nothing through the ordinary key properties — Besiege delivers it as emulation
  — and these blocks never looked. They do now, both the level that steers and
  the press and release edges the Toggle and S2S modes run on.
- Those edges are read on the physics step and latched, rather than polled from
  the frame loop, so a short variable pulse is not missed. Simulated against the
  naive scheme: a one-physics-step pulse was invisible below 60 fps, and is now
  delivered reliably down to 15.

**Changed**

- Rebuilt against current Besiege. `AddLimits` had gained a parameter since 2018,
  which is what stopped the mod compiling at all.
- The assembly is `Return2CenterAssembly.dll` rather than `C#.dll`.

The source was recovered from the shipped assembly — the original was lost. See
[docs/RECOVERY.md](docs/RECOVERY.md).

## 0.1.3

The last released Workshop version, built in 2018.
