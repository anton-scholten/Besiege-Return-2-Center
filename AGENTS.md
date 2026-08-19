# Working on this repository

Notes for anyone — human or AI — changing this mod. The [README](README.md) is
for people who just want to use it; nothing here needs repeating there.

How the C# was recovered from the shipped assembly, and how faithful the result
is, are in [docs/RECOVERY.md](docs/RECOVERY.md).

## Layout

The folder Besiege loads is `Return2Center/`, because that subfolder is the whole
of what gets uploaded to the Workshop. Everything beside it is not part of the
mod.

```
Return2Center/Mod.xml                  manifest: assembly, resources, block list
Return2Center/Hinge.xml                the steering hinge: mesh, colliders, module
Return2Center/SteeringBlock.xml        the steering block: mesh, colliders, module
Return2Center/Return2CenterAssembly.dll  built by tools/build.sh (checked in, the game loads it)
Return2Center/Resources/               the meshes and textures both blocks use
Return2Center/R2CScripts/Mod.cs        mod source; not read by the game
tools/build.sh                         compiles with Besiege's own compiler
tools/verify-build.sh                  the check to run after editing any .cs
tools/install.sh                       builds and installs into the game
docs/, Previous_stuff/                 notes and working files; not loaded by anything
```

`Return2Center/Return2CenterAssembly.dll` is committed on purpose. `Mod.xml` names
it as an `<Assembly>`, so a checkout has to carry a built one or the mod does not
load.

`R2CScripts/` sits inside `Return2Center/` so the sources travel with the mod
folder, the way Clippy, Git View and Moon do it. Besiege only reads what `Mod.xml`
names, so the `.cs` file there is ignored by the game; `tools/install.sh --copy`
strips it out of the copy it makes.

The 2018 release called its assembly `C#.dll` — the Visual Studio project name,
leaking. It is `Return2CenterAssembly.dll` now. Nothing but `Mod.xml` ever named
it, so the rename is invisible to saved machines.

## Hard rules

**Never change `<ID>` in `Mod.xml`.** The game generated it on first load, and changing
it breaks every saved machine that references the mod. The same goes for
`<ID>1</ID>` in `Hinge.xml` and `<ID>2</ID>` in `SteeringBlock.xml`, and for the
module name `R2CSteering`, which is spelled in four places that must agree: the
`[XmlRoot]` on the module class, the `AddBlockModule` call in `Mod.OnLoad`, and
the `<R2CSteering>` element inside `<Modules>` in each block XML. The `modid`
attribute on those elements is the same GUID again.

**Do not rename a mapper key.** The `key=` attributes in `<ModuleMapperTypes>`
(`"left"`, `"right"`, `"RotationalSpeed"`, `"tension"`, `"PushToggle"`), the
`"steering-limits"` passed to `AddLimits`, and the `"ModeMenuKey"` passed to
`AddMenu` are what a saved machine stores its settings under. Renaming one silently resets that
setting on every existing machine. `displayName` is only the label in the mapper
and is free to change.

**Do not reorder the mode menu.** A machine saves its choice as an *index* into
`{R2C, S2S, Normal}`, so inserting anything but at the end repoints every saved
block at a different mode. The `ModeReturnToCentre`/`ModeSideToSide`/`ModeNormal`
constants exist to make that visible in the `switch`.

**Run `./tools/verify-build.sh` after editing `Mod.cs`.** Besiege's compiler is
ancient — write C# 4: no interpolated strings, no `?.`, no `nameof`, no
expression-bodied members, and no `enum` declarations (they segfault it). That
last one is why the mode constants are `const int`.

**The five adding points in each block XML are the house standard; keep them.**
Top at `(0,0,1.0)`, and the four sides at `z=0.5` with `±0.5` offsets and their
matching `±90` rotations. They are the same list Sound Blocks, Special Effects
and Moon use, and they are what makes a modded block snap onto the same grid as a
base-game one. Both blocks already had them; unlike Moon, nothing needed fixing
here.

## Why it is built the way it is

**`System.Xml` is on the mod loader's blacklist and this mod references it
anyway.** That is not an oversight. `InternalModding.Assemblies.AssemblyScanner`
walks field types, method locals and IL operands; it never enumerates custom
attributes. The `[XmlRoot]` / `[XmlElement]` / `[XmlIgnore]` markers on
`R2CSteering` are metadata, so they pass, and they are the only way to name the
elements a block module deserialises. `tools/build.sh` runs a blacklist check over
every build rather than trusting that reasoning.

**Everything shared with the built-in blocks is copied from them, not
approximated.** Four numbers and one curve come straight out of
`SteeringWheel::Awake` and `SteeringWheel::Start` in `Assembly-CSharp.dll`, and
if any of them ever look wrong, that is where to re-read them:

| | built-in hinge | built-in steering block |
| --- | --- | --- |
| `<LimitsDisplay>` position | `(0, -0.342, 0)` | `(0, 0.1, 0)` |
| `<LimitsDisplay>` rotation | `(90, 0, 0)` | `(0, 0, 0)` |
| `<LimitsDisplay>` scale | `0.5` | `0.33` |
| tension slider | default 1, min 0.5, max 2, `logScaling` on | the same |

`<LimitsDisplay>` is what the mapper draws next to the limit dials, and it is the
*only* thing positioning it: `Selectors.LimitsSelector.Init` instantiates the
block's mesh and then overwrites its local position, rotation and scale from this
transform, so the `<Mesh>` block in the same XML has no say in it. The hinge's
90° about X is what turns it from end-on — which is a featureless plate — to the
side view that shows the barrel. Both blocks use the base-game meshes, so the
base-game numbers are correct for them.

**Tension is the sixth power of the slider.** `ApplyTension` scales the joint's
`positionSpring` and `positionDamper` by `t*t*t*t*t*t`, which is what turns a
0.5x-2x slider into a 1/64x-64x range of stiffness. That is `SteeringWheel.Start`
verbatim, and it is the whole point of the control — a linear multiplier would
make the slider do almost nothing.

It is applied twice: from `Start`, and again on the startup frame in
`SimulateUpdateHost`. `Start` runs once on a behaviour that Besiege then reuses
for later runs, so without the second call a tension changed between runs would
not take.

**The module is a fork of the game's own steering module.** `Modding.Modules.
Official.SteeringModule` / `SteeringModuleBehaviour` in `Assembly-CSharp.dll` is
the thing this was adapted from, and it is still the reference to read
when something here looks odd. Everything that is *not* the mode menu and the
push toggle should look like that class does today.

**Setup is deferred to the third simulated frame.** `SafeAwake` builds the mapper
controls, but the *values* in them — the limits, the mode — are not settled until
the machine has been simulating for a frame, hence the `hasStarted`/`startFrames`
dance at the top of `SimulateUpdateHost`. The official module does the same, for
the same reason.

**`AngleMin`/`AngleMax` are read once, at that third frame, and are not the same
thing as `limits.Min`/`limits.Max`.** The block applies two independent flips to
the limits range — `FlipInvert`, which is whether the *block* was mirrored, and
`FlipLimits`, which is a per-block XML setting because the hinge and the block
have their spin axes pointing opposite ways. The official module applies neither
and reads `limits.Min`/`limits.Max` straight. Do not "simplify" this to match it.

**The three modes differ only in what drives `input`.** All three end in the same
two operations — `Steer` to turn towards a demand and clamp at the limits, or
`ReturnToCentre` to wind back to zero without overshooting. In the 2018 assembly
those two bodies were written out three times each; they are one method each now.
See [docs/RECOVERY.md](docs/RECOVERY.md) for the check that says the six copies
were identical.

**`SetAngle0` exists so the last step back to centre reaches the joint.** The
joint is only written when `angleToBe != 0`, so without a flag the frame that
finally lands on exactly zero would be skipped and the block would stop a hair
off centre. The flag makes that one frame go through.

## What was changed, and what is left alone

The 2018 assembly was recovered faithfully first, and then changed in these ways.
Read this before "fixing" any of it back.

**`AddLimits` gained a seventh parameter.** Current Besiege has
`AddLimits(displayName, key, defaultMin, defaultMax, highestAngle, iconInfo,
enabled)`; the six-parameter overload the 2018 build called is gone. `true` is
passed, which is what the official steering module passes.

**Nothing was reset between simulation runs.** Besiege keeps the machine, and so
these behaviours, alive when you stop simulating, and `hasStarted` was set once
and never cleared. From the second run on, the limits, the flip and the mode were
whatever they had been on the first run — changing the mode in the mapper did
nothing until the machine was reloaded — and `angleToBe` still held the angle the
block had stopped at, so it snapped straight back there. `OnSimulateStart` now
winds all of it back.

**The block ignored emulated keys.** Modern Besiege lets one block drive another
by emulating its keys, and the official steering module reads that through
`MKey.EmulationValue()` in `KeyEmulationUpdate`. This one had no such override, so
nothing could steer it but a human. It has one now.

Only the *level* is read, never `EmulationPressed`/`EmulationReleased`:
`KeyEmulationUpdate` is driven from `Machine.FixedUpdate` while the rest of this
runs on `Update`, so an edge read there would be missed on some frames and seen
twice on others. The press and release edges the modes need are derived from the
level changing instead, which is frame-rate independent.

**`Text` is gone.** The module carried a public `string Text { get; set; }` that
nothing read, no block XML set, and the official module has never had — an
artefact of whatever the fork was taken from.

Left alone deliberately:

- **The joint is still driven from `SimulateUpdateHost`, not
  `SimulateFixedUpdateHost`.** The official module has since split the two —
  input on `Update`, physics on `FixedUpdate` — and that is the better shape. It
  is not done here because the mode state machine reacts to key *edges*, which
  only exist on `Update`, so the split needs the edges latched and drained rather
  than just moved. Worth doing; worth doing deliberately, with the game open.
- **`if (... && !HasRigidbody && Rigidbody.isKinematic) return;`** reads like a
  typo for `||` and is not one — it is copied verbatim from the official module,
  which still spells it exactly this way today.
- **`OnReload` dereferences `limits` without checking `Module.HasLimits`.** Also
  what the official module does. Both blocks set `HasLimits` true, so it cannot
  fire; it would matter only to a third block XML that did not.
