# How the source was recovered

The C# for this mod was lost; only the shipped assembly survived — `C#.dll`,
12,288 bytes, built 2018. `Return2Center/R2CScripts/Mod.cs` was reconstructed
from that assembly and then checked against it. This is the record of how, and of
how much the result can be trusted.

## The tooling

No .NET toolchain is installed on this machine and none was added. Everything
came out of the game's own `Besiege_Data/Managed`:

- **Reading the assembly**: `Mono.Cecil.dll`, which Besiege ships. A dumper walks
  the metadata — types, base types, fields with their flags, method signatures,
  custom attributes, locals, exception handlers — and prints every method body as
  an instruction list with branch targets resolved to the *ordinal* of the target
  instruction rather than a byte offset, so two builds with different encodings
  can still be compared line by line.
- **Running the dumper, and rebuilding the mod**: Besiege's own `mcs.dll`, driven
  offline through the game's `libmono.so`. That is what `tools/build.sh` does, and
  the same host runs the dumper against an assembly.

The same dumper was pointed at `Assembly-CSharp.dll` to read
`Modding.Modules.Official.SteeringModule` and `SteeringModuleBehaviour` — the
game's own steering module, which this mod is a fork of. Having the *current*
version of the thing the mod was forked from is most of what made updating it
straightforward: it says which API calls have changed shape and which of the odd
lines in the 2018 code are the mod's own and which are inherited.

## What the assembly gave up

Everything structural survives compilation and was read directly rather than
guessed at: the three types and their base types, every field with its type and
its `public`/`private` flags, every method signature and its accessibility, the
`[XmlRoot("R2CSteering")]` naming the module element, the per-field
`[XmlElement]` / `[XmlIgnore]` / `[DefaultValue]` / `[RequireToValidate]` /
`[Reloadable]` markers, the `Text` auto-property, and all four assembly
references.

What does **not** survive is what you would expect: local variable names,
parameter names of private methods, comments, and the file layout. Local names in
the reconstruction are chosen for readability.

Field names *did* survive, and they were a mix of casing conventions — `leftKey`
and `rightKey` beside `PushToggle` and `InputClamp`, `AngleMin` beside
`angleToBe`. The reconstruction kept them while it was being checked against the
assembly, and normalised them afterwards; they are private fields, so nothing
outside the assembly ever saw them. `SetAngle0` became `angleWasNonZero`,
`InputClamp` became `latchedInput`, `MenuChoice` became `mode`, and the rest lost
their capitals.

## The original was not built with Besiege's compiler

The 2018 assembly is a **Debug** build produced by Microsoft's C# compiler: it
carries `[Debuggable]` and every method is padded with `nop`. The rebuild is a
Release build from Besiege's `mcs`. A byte-for-byte comparison was never
available; what is available is a comparison of what each method *does*.

## How the reconstruction was checked

Both assemblies were dumped and compared method by method on their semantic
content: which members are called, which fields are read and written, and which
constants and strings appear — ignoring locals, branch encodings, stack shuffling
and conversions, since those are exactly what the two compilers are free to
disagree about. The systematic disagreements are:

| in the original (csc, Debug) | in the rebuild (mcs, Release) |
| --- | --- |
| `nop` between every statement | absent |
| every condition spilled: `stloc.N` / `ldloc.N` / `brfalse` | `brfalse` straight off the stack |
| `!x` as `ldc.i4.0` / `ceq`; `x != k` as `ceq` / `ldc.i4.0` / `ceq` | the fused `brtrue`, `bne.un`, `bge.un`, `ble.un` |
| `x == null` as `ldnull` / `ceq` / `brfalse` | `brtrue` |
| a three-case `switch` as a jump table | a chain of `beq` |
| a repeated constant hoisted into a local | the constant, repeated |

Every hand-written method matched except where it was changed on purpose. The
differences that remain are, in full:

| method | difference | why |
| --- | --- | --- |
| `get_Text`, `set_Text` | gone from the rebuild | dead property, see [AGENTS.md](../AGENTS.md#what-was-changed-and-what-is-left-alone) |
| `SafeAwake` | `AddLimits` takes a seventh argument | the six-argument overload no longer exists |
| `SimulateUpdateHost` | six inlined blocks became two methods | see below |
| `SimulateUpdateHost` | reads `emuLeftValue` / `emuRightValue` / `emuWasHeld` | key emulation support |
| `KeyEmulationUpdate` | new | variables and key emulation |
| `.ctor`, `SafeAwake` | `UnityEngine.Vector3` where the original built a `Modding.Serialization.Vector3` and let it convert | the conversion is a componentwise copy — checked, in `Vector3::op_Implicit` — so `Vector3.zero` and `new Vector3(1f, 0f, 0f)` are the same values by a shorter route |
| `SafeAwake` | one `MotionAbout` call per axis rather than three near-identical `switch` arms | free about the spin axis, locked about the other two — which is what the three arms each spelled out |
| `SimulateUpdateHost` | the startup frame is `Begin`, the toggle latch is `PushToggleInput`, the limit test is `AtLimit`, the per-frame step is `Rate` | each was written out two or three times |
| `SimulateUpdateHost` | `Quaternion.Euler(axis * angleToBe)` in place of three componentwise writes into a `jointEulerRotation` field | same product; the field held nothing between frames and is gone |
| `SafeAwake`, `Start` | the tension slider, and `ApplyTension` | new; see [AGENTS.md](../AGENTS.md) |

## The six blocks that became two methods

`SimulateUpdateHost` is a `switch` over three modes, and each mode ended with the
same pair of alternatives: turn towards the demand and clamp at the limits, or
wind back towards zero. The 2018 assembly writes both out in all three arms — six
blocks, and every one of them the same instruction sequence.

They were compared arm by arm before being replaced by `Steer` and
`ReturnToCentre`, on all of: the order the five multiplications happen in
(`input * deltaTime * 100 * TargetAngleSpeed * speed * FlipInvert`, left to
right, and the float result depends on that order), whether `FlipInvert` is in
the product at all (it is, when steering; it is not, when returning to centre),
the guard on the clamp (`Module.HasLimits && limits.IsActive`), and the clamp's
own shape (`< -angleMin` pins to `-angleMin`, `> angleMax` pins to `angleMax`).
All six agreed. The rebuilt `Steer` and `ReturnToCentre` were then read back out
of the new assembly and matched against the originals instruction for
instruction.

They have since been shortened further — the clamp is `Mathf.Clamp` and the wind
back to zero is `Mathf.MoveTowards`, which is what both were spelling out — along
with the rest of the tidy-up described below.

## Checking the tidy-up

The refactoring above is the kind that an IL comparison is no longer any use for:
six blocks becoming four methods changes every instruction in the method they
came out of. It was checked by simulation instead — both versions of the mode
logic transcribed into Python, one from the 2018 IL and one from the C# as it now
reads, and driven with 4,000 random traces of 400 frames each: random mode,
random key presses and releases, the toggle and the limits switched on and off
mid-trace, and a spread of speeds, frame times, limit ranges and flip states.
`angleToBe`, the latch, the captured input, and whether the joint was written
that frame were compared every frame.

1.6 million frames, no differences — and bit-exact, not merely within tolerance,
including where `Rate` regroups the five multiplications that set the step. The
four combinations of the limits flip were checked exhaustively against the
cascade of `if`s they replaced. The script is `.scratch/difftest.py`, which is
not committed; it is thirty lines of each version side by side and is quicker to
rewrite than to read.

## Reading the comparison the other way

Two things the check does **not** prove, and neither is a defect in the method:

- It says the reconstruction matches the 2018 assembly. It says nothing about
  whether the 2018 assembly was *correct* — and in the two places listed in
  [AGENTS.md](../AGENTS.md#what-was-changed-and-what-is-left-alone) it was not.
- Nothing here has been run in the game. The build is checked, the IL is checked,
  and the blacklist scanner that would make Besiege refuse the assembly is
  checked. Whether the block behaves is a question for a level.
