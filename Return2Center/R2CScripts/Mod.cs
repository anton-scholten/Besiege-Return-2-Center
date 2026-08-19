using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Serialization;
using Modding;
using Modding.Modules;
using Modding.Serialization;
using UnityEngine;

// Modding.Serialization also declares a Vector3. Everything below means Unity's.
using Vector3 = UnityEngine.Vector3;

namespace R2CSteering
{
    /// <summary>
    /// Return 2 Center: a steering hinge and a steering block that can be told to
    /// spring back to their neutral angle when the key is let go.
    ///
    /// The module is a variant of the game's own steering module
    /// (Modding.Modules.Official.SteeringModule) with a three-way mode menu and a
    /// hold/toggle switch added; see AGENTS.md for what differs and why.
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

            /// <summary>Which local axis the block turns about: X, Y or Z.</summary>
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

            protected override bool Validate(string elemName)
            {
                if (!base.Validate(elemName)) { return false; }
                if (!HasLimits) { return true; }

                // Everything the limits mapper needs has to be there once limits
                // are declared; a half-filled <R2CSteering> is a mod bug, not a
                // player one, so it is reported rather than defaulted.
                if (LimitsDisplay == null) { return MissingElement(elemName, "LimitsDisplay"); }
                if (!LimitsDefaultMinSpecified) { return MissingElement(elemName, "LimitsDefaultMin"); }
                if (!LimitsDefaultMaxSpecified) { return MissingElement(elemName, "LimitsDefaultMax"); }
                if (!LimitsHighestAngleSpecified) { return MissingElement(elemName, "LimitsHighestAngle"); }
                return true;
            }
        }

        public class R2CSteeringBehaviour : BlockModuleBehaviour<R2CSteering>
        {
            /// <summary>Mode menu entries. The saved value is the index, so never reorder these.</summary>
            const int ModeReturnToCentre = 0;
            const int ModeSideToSide = 1;
            const int ModeNormal = 2;

            /// <summary>Frames of simulation to wait before reading the mapper's settled values.</summary>
            const int StartDelayFrames = 3;

            MKey leftKey;
            MKey rightKey;
            MToggle PushToggle;
            bool PushToggleWasActive;
            MMenu ModeMenu;
            int MenuChoice;
            MSlider speedSlider;
            MSlider tensionSlider;
            MLimits limits;

            ConfigurableJoint myJoint;
            Vector3 axis;

            bool hasStarted;
            int startFrames;

            float input;
            float InputClamp;
            bool SetAngle0;
            bool KeyPressed;

            // Emulated key state, sampled in KeyEmulationUpdate. Levels only, never
            // edges: emulation runs on FixedUpdate and the rest of this runs on
            // Update, so an edge read here would be missed or seen twice.
            float emuLeftValue;
            float emuRightValue;
            bool emuWasHeld;

            float angleToBe;
            float AngleMin;
            float AngleMax;
            bool FlipLimits;

            Vector3 jointEulerRotation = Vector3.zero;

            /// <summary>-1 when the block has been mirrored, so it steers the way it looks.</summary>
            float FlipInvert { get { return Flipped ? -1f : 1f; } }

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
                    PushToggle = GetToggle(Module.PushToggle);
                    speedSlider = GetSlider(Module.SpeedSlider);
                    tensionSlider = GetSlider(Module.TensionSlider);
                    // The useful part of the range is below 1, so the handle is
                    // placed logarithmically — as on the base-game blocks.
                    tensionSlider.logScaling = true;
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
                        case Direction.X:
                            myJoint.angularXMotion = ConfigurableJointMotion.Free;
                            myJoint.angularYMotion = ConfigurableJointMotion.Locked;
                            myJoint.angularZMotion = ConfigurableJointMotion.Locked;
                            axis = new Vector3(1f, 0f, 0f);
                            break;
                        case Direction.Y:
                            myJoint.angularXMotion = ConfigurableJointMotion.Locked;
                            myJoint.angularYMotion = ConfigurableJointMotion.Free;
                            myJoint.angularZMotion = ConfigurableJointMotion.Locked;
                            axis = new Vector3(0f, 1f, 0f);
                            break;
                        case Direction.Z:
                            myJoint.angularXMotion = ConfigurableJointMotion.Locked;
                            myJoint.angularYMotion = ConfigurableJointMotion.Locked;
                            myJoint.angularZMotion = ConfigurableJointMotion.Free;
                            axis = new Vector3(0f, 0f, 1f);
                            break;
                    }
                }

                List<string> modes = new List<string>();
                modes.Add("R2C");
                modes.Add("S2S");
                modes.Add("Normal");
                ModeMenu = AddMenu("ModeMenuKey", ModeReturnToCentre, modes, false);
            }

            public void Start()
            {
                FlipLimits = Module.FlipLimits;
                if (!IsSimulating || !SimPhysics) { return; }

                if (HasRigidbody) { Rigidbody.maxAngularVelocity = Module.MaxAngularSpeed; }

                ApplyTension();

                myJoint.targetAngularVelocity = axis * 10f;
                myJoint.breakForce = 16500f;
                myJoint.breakTorque = 16500f;
            }

            /// <summary>
            /// Sets the joint's angular drive from the tension slider, the way
            /// SteeringWheel.Start does on the base-game blocks: a stiff, heavily
            /// damped drive that holds the angle it is told to rather than being
            /// spring-loaded about it, scaled by the sixth power of the slider.
            ///
            /// The sixth power is what turns a 0.5x-2x slider into a 1/64x-64x
            /// range of stiffness, which is the whole point of the control; do not
            /// "simplify" it to a linear multiplier.
            /// </summary>
            void ApplyTension()
            {
                // A stripped block has no joint to drive.
                if (!myJoint) { return; }

                float tension = tensionSlider.Value;
                tension = tension * tension * tension * tension * tension * tension;

                JointDrive angularYZDrive = myJoint.angularYZDrive;
                JointDrive angularXDrive = myJoint.angularXDrive;
                angularXDrive.positionDamper = 50f * tension;
                angularYZDrive.positionDamper = 50f * tension;
                angularXDrive.positionSpring = 100000f * tension;
                angularYZDrive.positionSpring = 100000f * tension;
                myJoint.angularYZDrive = angularYZDrive;
                myJoint.angularXDrive = angularXDrive;
            }

            /// <summary>
            /// Besiege keeps the behaviour alive between runs, so every bit of
            /// per-run state has to be wound back here or the second run starts
            /// where the first one stopped and ignores any setting changed since.
            /// </summary>
            public override void OnSimulateStart()
            {
                hasStarted = false;
                startFrames = 0;
                angleToBe = 0f;
                input = 0f;
                InputClamp = 0f;
                SetAngle0 = false;
                KeyPressed = false;
                PushToggleWasActive = false;
                emuLeftValue = 0f;
                emuRightValue = 0f;
                emuWasHeld = false;
            }

            /// <summary>Lets other blocks drive this one by emulating its keys.</summary>
            public override void KeyEmulationUpdate()
            {
                emuLeftValue = leftKey.EmulationValue();
                emuRightValue = rightKey.EmulationValue();
            }

            public override void SimulateUpdateHost()
            {
                if (!hasStarted)
                {
                    // The mapper's values are not settled on the first simulated
                    // frame, so the limits and the mode are read a few frames in.
                    if (startFrames != StartDelayFrames) { startFrames++; return; }

                    if (HasRigidbody) { Rigidbody.WakeUp(); }
                    hasStarted = true;

                    // Start() runs once, on the first run of a reused behaviour;
                    // this is what picks up a tension changed since then.
                    ApplyTension();

                    if (FlipInvert == 1f)
                    {
                        AngleMin = limits.Min;
                        AngleMax = limits.Max;
                    }
                    else if (FlipInvert == -1f)
                    {
                        AngleMin = limits.Max;
                        AngleMax = limits.Min;
                    }
                    if (FlipLimits)
                    {
                        float swap = AngleMin;
                        AngleMin = AngleMax;
                        AngleMax = swap;
                    }

                    MenuChoice = ModeMenu.Value;
                }

                if (!myJoint) { return; }

                Rigidbody connectedBody = myJoint.connectedBody;
                bool hasConnectedBody = connectedBody != null;
                if (hasConnectedBody && connectedBody.isKinematic && !HasRigidbody && Rigidbody.isKinematic)
                {
                    return;
                }

                bool emuHeld = emuLeftValue != 0f || emuRightValue != 0f;
                input = (leftKey.Value + emuLeftValue) - (rightKey.Value + emuRightValue);
                KeyPressed = leftKey.IsPressed || rightKey.IsPressed || (emuHeld && !emuWasHeld);
                bool keyReleased = leftKey.IsReleased || rightKey.IsReleased || (!emuHeld && emuWasHeld);
                emuWasHeld = emuHeld;

                float speed = speedSlider.Value;
                if (speed == 0f) { return; }

                switch (MenuChoice)
                {
                    case ModeReturnToCentre:
                        // Steer while held; let go and it winds back to zero.
                        if (PushToggle.IsActive)
                        {
                            if (KeyPressed)
                            {
                                PushToggleWasActive = !PushToggleWasActive;
                                InputClamp = input;
                            }
                            if (PushToggleWasActive)
                            {
                                input = InputClamp;
                                // Reaching a limit ends the toggled push, so the
                                // block does not sit straining against the stop.
                                if (limits.IsActive && (angleToBe == -AngleMin || angleToBe == AngleMax))
                                {
                                    input = 0f;
                                    PushToggleWasActive = false;
                                }
                            }
                        }

                        if (input != 0f) { Steer(input, speed); }
                        else { ReturnToCentre(speed); }
                        break;

                    case ModeSideToSide:
                        // Sweeps between the two limits, reversing at each end.
                        if (PushToggle.IsActive)
                        {
                            if (KeyPressed)
                            {
                                InputClamp = PushToggleWasActive ? 0f : input;
                                PushToggleWasActive = !PushToggleWasActive;
                            }
                        }
                        else
                        {
                            if (KeyPressed) { InputClamp = input; }
                            if (keyReleased) { InputClamp = 0f; }
                        }

                        if (limits.IsActive && (angleToBe == -AngleMin || angleToBe == AngleMax))
                        {
                            InputClamp = -InputClamp;
                        }

                        if (InputClamp != 0f) { Steer(InputClamp, speed); }
                        else { ReturnToCentre(speed); }
                        break;

                    case ModeNormal:
                        // The stock steering behaviour: the angle stays where it
                        // was left, unless the toggle is on and has been released.
                        if (PushToggle.IsActive)
                        {
                            if (KeyPressed)
                            {
                                PushToggleWasActive = !PushToggleWasActive;
                                InputClamp = input;
                            }
                            if (PushToggleWasActive) { input = InputClamp; }
                        }

                        if (input != 0f) { Steer(input, speed); }
                        else if (PushToggle.IsActive) { ReturnToCentre(speed); }
                        break;
                }

                // SetAngle0 makes the joint be told about the last step back to
                // zero; without it the block stops one frame short of centre.
                if (angleToBe == 0f && !SetAngle0) { return; }

                if (HasRigidbody && Rigidbody.IsSleeping()) { Rigidbody.WakeUp(); }
                if (hasConnectedBody && connectedBody.IsSleeping()) { connectedBody.WakeUp(); }

                jointEulerRotation.x = axis.x * angleToBe;
                jointEulerRotation.y = axis.y * angleToBe;
                jointEulerRotation.z = axis.z * angleToBe;
                myJoint.targetRotation = Quaternion.Euler(jointEulerRotation);

                SetAngle0 = angleToBe != 0f;
            }

            /// <summary>Turns towards <paramref name="direction"/>, stopping at the limits.</summary>
            void Steer(float direction, float speed)
            {
                angleToBe += direction * Time.deltaTime * 100f * Module.TargetAngleSpeed * speed * FlipInvert;

                if (!Module.HasLimits || !limits.IsActive) { return; }

                float min = -AngleMin;
                float max = AngleMax;
                if (angleToBe < min) { angleToBe = min; }
                else if (angleToBe > max) { angleToBe = max; }
            }

            /// <summary>Winds the angle back towards zero, without overshooting it.</summary>
            void ReturnToCentre(float speed)
            {
                float step = Time.deltaTime * 100f * Module.TargetAngleSpeed * speed;
                if (angleToBe > 0f)
                {
                    angleToBe -= step;
                    if (angleToBe < 0f) { angleToBe = 0f; }
                }
                else if (angleToBe < 0f)
                {
                    angleToBe += step;
                    if (angleToBe > 0f) { angleToBe = 0f; }
                }
            }

            public override void OnSave(XDataHolder data)
            {
                base.OnSave(data);
                data.Write("bmt-autoReturn", PushToggle.IsActive);
                data.Write("bmt-rotation-speed", speedSlider.Value);
                data.Write("bmt-limits", new float[] { limits.Min, limits.Max });
            }
        }
    }
}
