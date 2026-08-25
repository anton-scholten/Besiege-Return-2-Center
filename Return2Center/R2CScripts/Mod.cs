using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using Modding;
using Modding.Modules;
using Modding.Serialization;
using UnityEngine;

// Modding.Serialization declares a Vector3 too. Everything below means Unity's.
using Vector3 = UnityEngine.Vector3;

namespace R2CSteering
{
    /// <summary>
    /// Return 2 Center: a steering hinge and a steering block that spring back to
    /// centre when the key is let go.
    ///
    /// A fork of the game's own steering module (Modding.Modules.Official.
    /// SteeringModule) with a three-way mode menu and a hold/toggle switch added.
    /// AGENTS.md has the rest of the why.
    /// </summary>
    public class Mod : ModEntryPoint
    {
        public override void OnLoad()
        {
            CustomModules.AddBlockModule<R2CSteering, R2CSteeringBehaviour>("R2CSteering", false);
        }

        /// <summary>
        /// The &lt;R2CSteering&gt; element in Hinge.xml and SteeringBlock.xml.
        /// Field names are the XML element names; do not rename them.
        /// </summary>
        [XmlRoot("R2CSteering")]
        public class R2CSteering : BlockModule
        {
            [XmlElement, RequireToValidate] public MKeyReference LeftKey;
            [XmlElement, RequireToValidate] public MKeyReference RightKey;
            [XmlElement, RequireToValidate] public MSliderReference SpeedSlider;
            [XmlElement, RequireToValidate] public MSliderReference TensionSlider;
            [XmlElement, RequireToValidate] public MToggleReference PushToggle;

            /// <summary>The local axis the block turns about.</summary>
            [XmlElement] public Direction Axis;

            [XmlElement, Reloadable] public float MaxAngularSpeed;
            [XmlElement, Reloadable] public float TargetAngleSpeed;

            [XmlElement] public bool HasLimits;

            [XmlElement, DefaultValue(null), RequireToValidate, Reloadable]
            public TransformValues LimitsDisplay;

            [XmlElement, DefaultValue(0)] public float LimitsDefaultMin;
            [XmlIgnore] public bool LimitsDefaultMinSpecified;

            [XmlElement] public float LimitsDefaultMax;
            [XmlIgnore] public bool LimitsDefaultMaxSpecified;

            [XmlElement, Reloadable] public float LimitsHighestAngle;
            [XmlIgnore, Reloadable] public bool LimitsHighestAngleSpecified;

            /// <summary>Swaps which end of the limits range is "min" for this block.</summary>
            [XmlElement] public bool FlipLimits;

            /// <summary>
            /// A half-filled &lt;R2CSteering&gt; is a mod bug rather than a player
            /// one, so a missing limits element is reported, not defaulted.
            /// </summary>
            protected override bool Validate(string elemName)
            {
                if (!base.Validate(elemName)) { return false; }
                if (!HasLimits) { return true; }

                if (LimitsDisplay == null) { return MissingElement(elemName, "LimitsDisplay"); }
                if (!LimitsDefaultMinSpecified) { return MissingElement(elemName, "LimitsDefaultMin"); }
                if (!LimitsDefaultMaxSpecified) { return MissingElement(elemName, "LimitsDefaultMax"); }
                if (!LimitsHighestAngleSpecified) { return MissingElement(elemName, "LimitsHighestAngle"); }
                return true;
            }
        }

        public class R2CSteeringBehaviour : BlockModuleBehaviour<R2CSteering>
        {
            /// <summary>Mode menu entries. A machine saves the index, so never reorder these.</summary>
            const int ModeReturnToCentre = 0;
            const int ModeSideToSide = 1;
            const int ModeNormal = 2;

            /// <summary>Simulated frames to wait before reading the mapper's settled values.</summary>
            const int StartDelayFrames = 3;

            MKey leftKey;
            MKey rightKey;
            MToggle pushToggle;
            MMenu modeMenu;
            MSlider speedSlider;
            MSlider tensionSlider;
            MLimits limits;

            ConfigurableJoint myJoint;
            Vector3 axis;

            int mode;
            bool hasStarted;
            int startFrames;

            float input;
            bool keyPressed;
            bool keyReleased;

            /// <summary>The direction a latched push is steering in. See PushToggleInput.</summary>
            float latchedInput;
            bool pushLatched;

            // What a variable, or another block, is driving the two keys with.
            // Sampled and latched in KeyEmulationUpdate; taken by ReadInput.
            float emuLeftValue;
            float emuRightValue;
            bool emuPressed;
            bool emuReleased;

            /// <summary>Angle demanded of the joint, in degrees about <see cref="axis"/>.</summary>
            float angleToBe;

            /// <summary>Limit stops, as magnitudes: the range is -angleMin .. angleMax.</summary>
            float angleMin;
            float angleMax;
            bool flipLimits;

            /// <summary>Whether the joint still needs telling about a step that landed on zero.</summary>
            bool angleWasNonZero;

            /// <summary>-1 when the block has been mirrored, so it steers the way it looks.</summary>
            float FlipInvert { get { return Flipped ? -1f : 1f; } }

            /// <summary>True when the angle is sitting on one of the limit stops.</summary>
            bool AtLimit()
            {
                return limits.IsActive && (angleToBe == -angleMin || angleToBe == angleMax);
            }

            /// <summary>Degrees to turn this frame at full demand.</summary>
            float Rate(float speed)
            {
                return Time.deltaTime * 100f * Module.TargetAngleSpeed * speed;
            }

            public override void OnReload()
            {
                if (HasRigidbody) { Rigidbody.maxAngularVelocity = Module.MaxAngularSpeed; }
                limits.MaxValue = Module.LimitsHighestAngle;
                limits.iconInfo = Module.LimitsDisplay.ToFauxTransform();
            }

            public override void SafeAwake()
            {
                if (IsSimulating && !SimPhysics) { return; }

                try
                {
                    leftKey = GetKey(Module.LeftKey);
                    rightKey = GetKey(Module.RightKey);
                    pushToggle = GetToggle(Module.PushToggle);
                    speedSlider = GetSlider(Module.SpeedSlider);
                    tensionSlider = GetSlider(Module.TensionSlider);
                    tensionSlider.logScaling = true;   // as on the base-game blocks
                    if (Module.HasLimits)
                    {
                        limits = AddLimits("Limits", "steering-limits",
                            Module.LimitsDefaultMin, Module.LimitsDefaultMax,
                            Module.LimitsHighestAngle,
                            Module.LimitsDisplay.ToFauxTransform(), true);
                    }
                }
                catch (Exception e)
                {
                    ModConsole.Log("Could not get all mapper types for R2CSteering Module! Module will be disabled.");
                    ModConsole.Log(e.ToString());
                    Destroy(this);
                    return;
                }

                if (!IsStripped)
                {
                    myJoint = GetComponent<ConfigurableJoint>();
                    switch (Module.Axis)
                    {
                        case Direction.X: axis = new Vector3(1f, 0f, 0f); break;
                        case Direction.Y: axis = new Vector3(0f, 1f, 0f); break;
                        case Direction.Z: axis = new Vector3(0f, 0f, 1f); break;
                    }
                    // Free to turn about the spin axis, locked about the other two.
                    myJoint.angularXMotion = MotionAbout(axis.x);
                    myJoint.angularYMotion = MotionAbout(axis.y);
                    myJoint.angularZMotion = MotionAbout(axis.z);
                }

                List<string> modes = new List<string>();
                modes.Add("R2C");
                modes.Add("S2S");
                modes.Add("Normal");
                modeMenu = AddMenu("ModeMenuKey", ModeReturnToCentre, modes, false);
            }

            static ConfigurableJointMotion MotionAbout(float component)
            {
                return component != 0f ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            }

            public void Start()
            {
                flipLimits = Module.FlipLimits;
                if (!IsSimulating || !SimPhysics) { return; }

                if (HasRigidbody) { Rigidbody.maxAngularVelocity = Module.MaxAngularSpeed; }
                ApplyTension();

                myJoint.targetAngularVelocity = axis * 10f;
                myJoint.breakForce = 16500f;
                myJoint.breakTorque = 16500f;
            }

            /// <summary>
            /// Drives the joint at the tension slider's sixth power, as
            /// SteeringWheel.Start does on the base-game blocks. The sixth power is
            /// what turns a 0.5x-2x slider into 1/64x-64x of stiffness; a linear
            /// multiplier would make the control do almost nothing.
            /// </summary>
            void ApplyTension()
            {
                if (!myJoint) { return; }   // a stripped block has no joint

                float t = tensionSlider.Value;
                float tension = t * t * t * t * t * t;

                JointDrive yz = myJoint.angularYZDrive;
                JointDrive x = myJoint.angularXDrive;
                x.positionDamper = 50f * tension;
                yz.positionDamper = 50f * tension;
                x.positionSpring = 100000f * tension;
                yz.positionSpring = 100000f * tension;
                myJoint.angularYZDrive = yz;
                myJoint.angularXDrive = x;
            }

            /// <summary>
            /// Lets a variable, or another block, drive this one through its keys.
            /// Besiege routes both down the same path: a key bound to a variable
            /// reports nothing through Value, IsPressed or IsHeld and turns up here.
            ///
            /// This runs once per fixed step, which is the cadence MKey advances its
            /// emulation snapshot at, so the edges are read here and latched for the
            /// next SimulateUpdateHost. Read from Update instead they would repeat
            /// across the frames sharing a fixed step, and a pulse that began and
            /// ended between two frames would be missed altogether.
            /// </summary>
            public override void KeyEmulationUpdate()
            {
                // A client without physics never built the mapper controls.
                if (leftKey == null) { return; }

                emuLeftValue = leftKey.EmulationValue();
                emuRightValue = rightKey.EmulationValue();

                // All four every step, and none of them behind a short-circuit: a
                // key whose edge methods go uncalled for a step is left comparing
                // against a stale snapshot on the next one.
                bool leftPressed = leftKey.EmulationPressed();
                bool rightPressed = rightKey.EmulationPressed();
                bool leftReleased = leftKey.EmulationReleased();
                bool rightReleased = rightKey.EmulationReleased();

                emuPressed = emuPressed || leftPressed || rightPressed;
                emuReleased = emuReleased || leftReleased || rightReleased;
            }

            /// <summary>
            /// MKey.IsReleased is the one key property that does not test useMessage,
            /// so a key bound to a variable still reports the release of the keyboard
            /// key it kept. Value, IsPressed and IsHeld all guard it; this restores
            /// the symmetry, or tapping the old key would stop an S2S sweep.
            /// </summary>
            static bool ReleasedOnKeyboard(MKey key)
            {
                return !key.useMessage && key.IsReleased;
            }

            /// <summary>
            /// Samples the two keys, folding in whatever is being emulated, and
            /// takes the latched emulation edges. Called on every update so a latch
            /// cannot survive a frame the block bailed out of and fire late.
            /// </summary>
            void ReadInput()
            {
                input = (leftKey.Value + emuLeftValue) - (rightKey.Value + emuRightValue);
                keyPressed = leftKey.IsPressed || rightKey.IsPressed || emuPressed;
                keyReleased = ReleasedOnKeyboard(leftKey) || ReleasedOnKeyboard(rightKey) || emuReleased;
                emuPressed = false;
                emuReleased = false;
            }

            public override void SimulateUpdateHost()
            {
                ReadInput();
                if (!hasStarted && !Begin()) { return; }
                if (!myJoint) { return; }

                Rigidbody connectedBody = myJoint.connectedBody;
                bool hasConnectedBody = connectedBody != null;
                // Copied from the official module, `&&` and all: nothing to drive
                // when both ends of the joint are frozen.
                if (hasConnectedBody && connectedBody.isKinematic && !HasRigidbody && Rigidbody.isKinematic)
                {
                    return;
                }

                float speed = speedSlider.Value;
                if (speed == 0f) { return; }

                switch (mode)
                {
                    case ModeReturnToCentre:
                        // Steer while held, and wind back to zero on release.
                        if (pushToggle.IsActive)
                        {
                            input = PushToggleInput();
                            // A latched push ends at a limit rather than sitting
                            // there straining against the stop.
                            if (pushLatched && AtLimit())
                            {
                                input = 0f;
                                pushLatched = false;
                            }
                        }
                        if (input != 0f) { Steer(input, speed); }
                        else { ReturnToCentre(speed); }
                        break;

                    case ModeSideToSide:
                        // Sweeps between the limits, reversing at each end.
                        if (pushToggle.IsActive)
                        {
                            // Unlike the other two modes, a second press stops it
                            // there and then rather than handing back the raw keys.
                            if (keyPressed)
                            {
                                latchedInput = pushLatched ? 0f : input;
                                pushLatched = !pushLatched;
                            }
                        }
                        else
                        {
                            if (keyPressed) { latchedInput = input; }
                            if (keyReleased) { latchedInput = 0f; }
                        }
                        if (AtLimit()) { latchedInput = -latchedInput; }

                        if (latchedInput != 0f) { Steer(latchedInput, speed); }
                        else { ReturnToCentre(speed); }
                        break;

                    case ModeNormal:
                        // Stock behaviour: the angle stays where it was left.
                        if (pushToggle.IsActive) { input = PushToggleInput(); }

                        if (input != 0f) { Steer(input, speed); }
                        else if (pushToggle.IsActive) { ReturnToCentre(speed); }
                        break;
                }

                if (angleToBe == 0f && !angleWasNonZero) { return; }

                if (HasRigidbody && Rigidbody.IsSleeping()) { Rigidbody.WakeUp(); }
                if (hasConnectedBody && connectedBody.IsSleeping()) { connectedBody.WakeUp(); }
                myJoint.targetRotation = Quaternion.Euler(axis * angleToBe);

                angleWasNonZero = angleToBe != 0f;
            }

            /// <summary>
            /// The mapper's values are not settled on the first simulated frame, so
            /// the limits, the mode and the tension are read a few frames in.
            /// Returns false while still counting down.
            /// </summary>
            bool Begin()
            {
                if (startFrames != StartDelayFrames) { startFrames++; return false; }

                if (HasRigidbody) { Rigidbody.WakeUp(); }
                hasStarted = true;

                // Two independent flips: FlipInvert is whether the block was
                // mirrored, flipLimits is a per-block XML setting because the hinge
                // and the block have their spin axes pointing opposite ways.
                bool swapped = FlipInvert == -1f;
                if (swapped != flipLimits)
                {
                    angleMin = limits.Max;
                    angleMax = limits.Min;
                }
                else
                {
                    angleMin = limits.Min;
                    angleMax = limits.Max;
                }

                mode = modeMenu.Value;
                return true;
            }

            /// <summary>
            /// Press-once-to-steer. Each press flips the latch and captures the
            /// direction; while latched, that captured direction is the demand, so
            /// the block keeps steering after the key is let go.
            /// </summary>
            float PushToggleInput()
            {
                if (keyPressed)
                {
                    pushLatched = !pushLatched;
                    latchedInput = input;
                }
                return pushLatched ? latchedInput : input;
            }

            /// <summary>Turns towards <paramref name="direction"/>, stopping at the limits.</summary>
            void Steer(float direction, float speed)
            {
                angleToBe += direction * Rate(speed) * FlipInvert;
                if (Module.HasLimits && limits.IsActive)
                {
                    angleToBe = Mathf.Clamp(angleToBe, -angleMin, angleMax);
                }
            }

            /// <summary>Winds the angle back to zero, without overshooting it.</summary>
            void ReturnToCentre(float speed)
            {
                angleToBe = Mathf.MoveTowards(angleToBe, 0f, Rate(speed));
            }

            public override void OnSave(XDataHolder data)
            {
                base.OnSave(data);
                data.Write("bmt-autoReturn", pushToggle.IsActive);
                data.Write("bmt-rotation-speed", speedSlider.Value);
                data.Write("bmt-limits", new float[] { limits.Min, limits.Max });
            }
        }
    }
}
