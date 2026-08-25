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
module name `R2CSteering`, which has to be spelled the same in the `[XmlRoot]` on
the module class, in the `AddBlockModule` call in `Mod.OnLoad`, and in the
`<R2CSteering>` element inside `<Modules>` in *both* block XMLs. The `modid`
attribute on those two elements is the mod `<ID>` again, not the module name.

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

It is applied from `Start`, which runs once per simulation run because the run is
a fresh clone — see the correction below. An earlier pass applied it a second time
on the startup frame, for a reason that turned out not to exist.

**The module is a fork of the game's own steering module.** `Modding.Modules.
Official.SteeringModule` / `SteeringModuleBehaviour` in `Assembly-CSharp.dll` is
the thing this was adapted from, and it is still the reference to read
when something here looks odd. Everything that is *not* the mode menu and the
push toggle should look like that class does today.

**Setup is deferred to the third simulated frame, and only half of that is
inherited.** The official module counts the same three frames, but it counts them
only to delay one `Rigidbody.WakeUp()`, and it goes on reading its keys while it
counts. The 2018 code moved the limits and the mode read into that block as well
and made it `return` until the count is up — so this block is inert for its first
three simulated frames where the base-game one is not.

Why the values are read there rather than in `Start` is not recorded anywhere and
was not recoverable from the assembly; keep it unless you can show the mapper is
settled earlier, because being wrong about that is a block that silently uses
stale limits.

**`angleMin`/`angleMax` are read once, in `Begin`, and are not the same thing as
`limits.Min`/`limits.Max`.** The block applies two independent flips to the limits
range — `FlipInvert`, which is whether the *block* was mirrored, and `FlipLimits`,
which is a per-block XML setting because the hinge and the block have their spin
axes pointing opposite ways. Either one alone swaps the ends; both together cancel,
which is why `Begin` tests them with `!=`. The official module applies neither and
reads `limits.Min`/`limits.Max` straight. Do not "simplify" this to match it.

They are magnitudes, not signed bounds: the usable range is `-angleMin`
to `angleMax`.

**The three modes differ only in what drives `input`.** All three end in the same
two operations — `Steer` to turn towards a demand and clamp at the limits, or
`ReturnToCentre` to wind back to zero without overshooting. In the 2018 assembly
those two bodies were written out three times each, along with the limit test and
the toggle latch; they are `Steer`, `ReturnToCentre`, `AtLimit` and
`PushToggleInput` now. See [docs/RECOVERY.md](docs/RECOVERY.md) for the check that
says the copies were identical, and for the simulation that says the refactoring
kept them that way.

The one mode that does *not* share the latch is S2S, and that is not an oversight:
a second press stops it where it stands, where R2C and Normal hand control back to
the raw keys and go on steering until you let go.

**`angleWasNonZero` exists so the last step back to centre reaches the joint.** The
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

**A correction, kept here because it was believed and acted on.** An earlier pass
added an `OnSimulateStart` that wound `hasStarted`, `angleToBe` and the rest back,
on the belief that Besiege keeps a block behaviour alive between runs and that the
2018 code was therefore stale from the second run on. That is wrong twice over, and
both halves are checkable in a minute:

- `Machine.StartSimulation` ends with `Object.Instantiate` of `buildingMachine`
  into `simulationClone`, and `SoftEndSimulation` — the only ender — calls
  `DestroySimMachine`. **Every run gets a fresh clone with fresh components.** No
  private field survives a run, so there was nothing to wind back.
- `Modding.ModBlockBehaviour.OnSimulateStart` has exactly one caller:
  `InternalModding.Blocks.ModBlockBehaviourHandler.Start`, guarded by
  `isSimulating`. It is not a per-run event *distinct* from `Start` — it is `Start`.
  So had the premise been true, the "fix" would not have run either.

The reset is gone, and so is the second `ApplyTension` call that existed only to
pick up a tension changed since a previous run. `Start` runs on every run and
applies the current value. `OnSimulateStop` is likewise `OnDisable`, not a
separate event.

**`MKey.IsReleased` is the one key property that does not test `useMessage`.**
`get_Value`, `get_IsPressed` and `get_IsHeld` all return 0/false the moment a key
is bound to a variable. `get_IsReleased` guards on `ignored`, on `Value > 0` and on
`MouseKeyBlocked()` — and not on `useMessage`. Binding a variable leaves the key's
keycodes in place (`KeySelector.SetVariable` does not clear them), so the physical
key still raises a release edge on a key the block is supposed to have stopped
listening to. In S2S that stopped a sweep when the player touched the old arrow key.
`ReleasedOnKeyboard` restores the symmetry the other three have built in.

**The block ignored emulated keys, and so ignored variables.** Besiege lets a key
be bound to a *variable* instead of a keyboard key, and lets one block drive
another's keys; both arrive down the same path. `KeyInputController.Emulate` walks
`usedMessages[name]` and calls `MKey.UpdateEmulation` on every key listening to
that variable, so a variable-driven key is simply a key with `Emulating` true.

That matters because a key bound to a variable reports **nothing** through the
ordinary properties: `MKey.get_Value`, `get_IsPressed` and `get_IsHeld` all test
`useMessage` and return 0/false when it is set. A block that reads only those is
inert under a variable, which is what this one was.

So the level is folded in from `MKey.EmulationValue()`, the way the official
steering module does, and the edges come from `EmulationPressed()` /
`EmulationReleased()`.

**The emulation edges are read on the fixed step and latched, not polled from
`Update`.** `MKey.CheckEmulation` keeps a snapshot it advances once per
`Time.fixedTime`, and `KeyEmulationUpdate` is the only hook that runs at that
cadence — `Machine.FixedUpdate` calls it through `EmulationUpdateBlock`. Polling
the edge methods from `SimulateUpdateHost` instead, which is what the official
*shooting* module does, gets it wrong both ways: at high frame rates every frame
sharing one fixed step sees the same press, and at low ones a pulse that begins
and ends between two frames is never seen at all. The shooting module gets away
with it because a rate limiter swallows the repeats; a toggle latch would not.

So `KeyEmulationUpdate` latches, `ReadInput` takes and clears. Simulated against
the old scheme, a one-step variable pulse was invisible below 60 fps and is now
delivered down to 15; see the CHANGELOG.

Two things there are deliberate and look like they could be tidied:

- All four edge calls are made every step, into locals, before any `||`. Each
  `MKey` advances its own snapshot only when one of *its* edge methods is called,
  so short-circuiting past the right-hand key leaves it comparing against a stale
  step on the next one. `EmulationValue()` does not advance anything, so it is no
  substitute.
- `ReadInput` is called before every early return in `SimulateUpdateHost`, not
  after the guards. A latch that survives a frame the block bailed out of would
  fire late, on a frame whose `input` no longer matches it.

`KeyEmulationUpdate` returns early when `leftKey` is null. `SafeAwake` builds no
mapper controls on a simulating client without physics, and `EmulationUpdateBlock`
checks only `isSimulating` — so on such a client this would otherwise throw every
other fixed step. The official steering module has the same hole.

Nothing needs declaring for any of this. `BlockPrefabCreator.SetupBehaviour` sets
`RegisterEmulationUpdate` true on every modded block, and `Machine.InitBlocks`
turns the whole pipeline on as soon as the machine holds one block that emulates
keys — which is exactly when a variable can do anything.

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
