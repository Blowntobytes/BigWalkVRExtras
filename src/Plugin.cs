using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BigWalkVR;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BigWalkVR")]

// Lets this assembly touch the VR mod's private/internal members at runtime
// (we compile against a publicized copy of BigWalkVR.dll).
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) { AssemblyName = assemblyName; }
        public string AssemblyName { get; }
    }
}

namespace BigWalkVRExtras
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.circuit.bigwalkvr", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class ExtrasPlugin : BasePlugin
    {
        public const string Guid = "blowntobytes.bigwalkvr.extras";
        public const string Name = "Big Walk VR Extras";
        public const string Version = "1.0.0";

        internal static ManualLogSource Logger;
        internal static ConfigEntry<bool> AutoSprint;
        internal static ConfigEntry<bool> StickClickSit;
        internal static ConfigEntry<bool> HoldToRecalibrate;
        internal static ConfigEntry<float> HoldSeconds;
        internal static ConfigEntry<bool> GripToggle;
        internal static ConfigEntry<int> VirtualCrouchMode; // 0 = off, 1 = hold, 2 = toggle
        internal static ConfigEntry<float> VirtualCrouchThreshold;
        internal static ConfigEntry<float> VirtualCrouchCameraDrop;

        private Harmony harmony;

        public override void Load()
        {
            Logger = Log;

            AutoSprint = Config.Bind("BigWalkVRExtras", "AutoSprint", false,
                "Always sprint while the left joystick is pushed (no stick click needed). Toggle in Settings > Big Walk VR > AUTO SPRINT.");
            StickClickSit = Config.Bind("BigWalkVRExtras", "RightStickClickSit", true,
                "Right joystick click toggles the sitting pose instead of recalibrating height.");
            HoldToRecalibrate = Config.Bind("BigWalkVRExtras", "HoldRightStickToRecalibrate", false,
                "When true: short click = sit/stand, holding the right stick click recalibrates height (the original behaviour).");
            HoldSeconds = Config.Bind("BigWalkVRExtras", "HoldSeconds", 0.5f,
                "How long the right stick must be held to recalibrate height (only if HoldRightStickToRecalibrate is true).");

            GripToggle = Config.Bind("BigWalkVRExtras", "GripToggle", false,
                "Grip toggle: press grip once to grab and keep holding an item, press grip again to release it. Toggle in Settings > Big Walk VR > GRIP MODE.");

            VirtualCrouchMode = Config.Bind("BigWalkVRExtras", "VirtualCrouchMode", 0,
                "Virtual crouch with the right joystick pulled back (towards you). 0 = off, 1 = hold (crouch while pulled), 2 = toggle (pull once to crouch, pull again to stand). Set in Settings > Big Walk VR > VIRTUAL CROUCH.");
            VirtualCrouchThreshold = Config.Bind("BigWalkVRExtras", "VirtualCrouchThreshold", 0.6f,
                "How far back the right joystick must be pulled (0..1) to crouch.");

            VirtualCrouchCameraDrop = Config.Bind("BigWalkVRExtras", "VirtualCrouchCameraDrop", 0.45f,
                "How far (metres) the VR camera is lowered while virtually crouching. 0 disables the camera drop.");

            // Fix the pause-menu control hint ("Reset height  RIGHT STICK").
            try
            {
                var controls = VrSettingsMenu.PauseControls;
                for (int i = 0; i < controls.Length; i++)
                {
                    if (controls[i].Item1 == "Reset height")
                        controls[i] = (HoldToRecalibrate.Value ? "Sit / stand (hold: reset height)" : "Sit / stand", "RIGHT STICK");
                    else if (controls[i].Item1 == "Grab" && GripToggle.Value)
                        controls[i] = ("Grab / release (toggle)", "GRIP");
                    else if (controls[i].Item1 == "Crouch / sit" && VirtualCrouchMode.Value != 0)
                        controls[i] = ("Crouch / sit", "CROUCH IRL / R STICK BACK");
                }
            }
            catch (Exception e) { Logger.LogWarning("Could not update pause control hints: " + e.Message); }

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(ExtrasPlugin).Assembly);
            Logger.LogInfo($"{Name} {Version} loaded (AutoSprint={AutoSprint.Value}, StickClickSit={StickClickSit.Value}, GripToggle={GripToggle.Value}, VirtualCrouch={VirtualCrouchMode.Value}).");
        }

        public override bool Unload()
        {
            harmony?.UnpatchSelf();
            return true;
        }
    }

    // ------------------------------------------------------------------
    //  Locomotion: right-stick-click sit + auto sprint
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(VrLocomotion))]
    internal static class LocomotionPatches
    {
        private static bool wasPressed;
        private static bool longFired;
        private static float pressStart;
        private static bool sitToggleRequested;

        // Virtual sit state (sitting while physically standing).
        private static bool virtualSit;
        private static bool virtualSitConfirmed;
        private static float virtualSitTime;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(VrLocomotion.Update))]
        private static void UpdatePrefix(ref VrControllerState rightController, bool suppressMovement)
        {
            if (!ExtrasPlugin.StickClickSit.Value) return;

            bool pressed = rightController.StateIsValid && rightController.JoystickPressed;
            float now = Time.unscaledTime;

            if (pressed && !wasPressed)
            {
                pressStart = now;
                longFired = false;
            }

            bool passThrough = false;
            if (ExtrasPlugin.HoldToRecalibrate.Value)
            {
                if (pressed && !longFired && now - pressStart >= ExtrasPlugin.HoldSeconds.Value)
                {
                    longFired = true;
                    passThrough = true; // let the VR mod see one click -> recalibrate height
                }
                else if (!pressed && wasPressed && !longFired && !suppressMovement)
                {
                    sitToggleRequested = true;
                }
            }
            else if (pressed && !wasPressed && !suppressMovement)
            {
                sitToggleRequested = true;
            }

            wasPressed = pressed;
            if (!passThrough) rightController.JoystickClickActive = 0; // swallow the click
        }

        private static bool lastCrouchPressed;
        private static bool lastStickBack;
        private static bool toggleCrouch;
        private static bool virtualCrouchActive;
        private static float crouchCameraBlend; // 0 = standing, 1 = fully lowered

        [HarmonyPostfix]
        [HarmonyPatch(nameof(VrLocomotion.Update))]
        private static void UpdatePostfix(VrControllerState rightController, bool suppressMovement)
        {
            var player = WorldManager.localPlayerCharacter;
            if (player == null) { sitToggleRequested = false; return; }

            var sitter = player.sitter;
            bool sittingNow = sitter != null && sitter.isSittingCorrected;

            if (suppressMovement)
            {
                sitToggleRequested = false;
                return;
            }

            // --- Sit / stand on right stick click ---
            if (ExtrasPlugin.StickClickSit.Value)
            {
                if (sitToggleRequested)
                {
                    sitToggleRequested = false;
                    virtualSit = !sittingNow;
                    virtualSitConfirmed = false;
                    virtualSitTime = Time.unscaledTime;
                    PulseRightHaptic();
                }

                if (virtualSit)
                {
                    if (sittingNow) virtualSitConfirmed = true;
                    else if (virtualSitConfirmed || Time.unscaledTime - virtualSitTime > 2f)
                        virtualSit = false; // stood up some other way (jump, etc.)
                }

                bool wantSit = VrLocomotion.physicalSitActive || virtualSit;
                VrLocomotion.sitInputPressed = wantSit != sittingNow;
            }

            // --- Virtual crouch: right stick pulled back ---
            int crouchMode = ExtrasPlugin.VirtualCrouchMode.Value;
            if (crouchMode != 0)
            {
                bool stickBack = rightController.StateIsValid
                    && rightController.Joystick.y < -Mathf.Clamp(ExtrasPlugin.VirtualCrouchThreshold.Value, 0.2f, 0.95f);
                bool virtualCrouch;
                if (crouchMode == 2)
                {
                    if (stickBack && !lastStickBack) toggleCrouch = !toggleCrouch; // each pull flips it
                    if (VrLocomotion.jumpPressedThisFrame) toggleCrouch = false;    // jumping stands you up
                    virtualCrouch = toggleCrouch;
                }
                else
                {
                    toggleCrouch = false;
                    virtualCrouch = stickBack;
                }
                lastStickBack = stickBack;

                virtualCrouchActive = virtualCrouch;
                bool crouch = VrLocomotion.crouchPressed || virtualCrouch;
                VrLocomotion.crouchPressedThisFrame = crouch && !lastCrouchPressed;
                VrLocomotion.crouchPressed = crouch;
                lastCrouchPressed = crouch;
            }
            else
            {
                toggleCrouch = false;
                lastStickBack = false;
                virtualCrouchActive = false;
                lastCrouchPressed = VrLocomotion.crouchPressed;
            }
            // Smoothly lower / raise the camera for virtual crouch (real crouching lowers your head by itself).
            crouchCameraBlend = Mathf.MoveTowards(crouchCameraBlend, virtualCrouchActive ? 1f : 0f, Time.unscaledDeltaTime / 0.2f);

            // --- Auto sprint ---
            if (ExtrasPlugin.AutoSprint.Value && !sittingNow && VrLocomotion.joystickMoveInput.sqrMagnitude > 0f)
            {
                VrLocomotion.sprintEnabled = true;
            }
        }

        // The VR rig is placed at (camera - rotation * TrackingOrigin); raising the origin lowers the view.
        [HarmonyPostfix]
        [HarmonyPatch("get_TrackingOrigin")]
        private static void TrackingOriginPostfix(ref Vector3 __result)
        {
            if (crouchCameraBlend > 0f)
                __result.y += crouchCameraBlend * Mathf.Max(0f, ExtrasPlugin.VirtualCrouchCameraDrop.Value);
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(VrLocomotion.Reset))]
        private static void ResetPostfix()
        {
            wasPressed = false;
            longFired = false;
            sitToggleRequested = false;
            virtualSit = false;
            virtualSitConfirmed = false;
            lastCrouchPressed = false;
            lastStickBack = false;
            toggleCrouch = false;
            virtualCrouchActive = false;
            crouchCameraBlend = 0f;
        }

        private static void PulseRightHaptic()
        {
            try { BigWalkVrRuntime.renderer?.openVr?.PulseRightHaptic(); }
            catch { /* haptics are optional */ }
        }
    }

    // ------------------------------------------------------------------
    //  Grip toggle: press grip to grab, press grip again to release
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(VrGrabbing))]
    internal static class GripTogglePatches
    {
        private enum HandState { Idle, Held, Masked }

        private sealed class Hand
        {
            public HandState State;
            public bool Armed;        // physical grip has been released since the grab began
            public bool SecondPress;  // grip squeezed again while armed; item drops when it is let go
            public bool WasPressed;
        }

        private static readonly Hand left = new Hand();
        private static readonly Hand right = new Hand();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(VrGrabbing.Update))]
        private static void UpdatePrefix(VrGrabbing __instance, ref VrControllerState leftController, ref VrControllerState rightController)
        {
            if (!ExtrasPlugin.GripToggle.Value)
            {
                if (left.State != HandState.Idle || right.State != HandState.Idle) ResetState();
                return;
            }

            Prop held = __instance.heldProp;
            bool holding = held != null;
            var holdingHand = __instance.holdingHand;

            // Track which hand (if any) currently holds the prop according to the VR mod.
            SyncHand(left, holding && holdingHand == TrackedControllerRole.LeftHand);
            SyncHand(right, holding && holdingHand == TrackedControllerRole.RightHand);

            Apply(left, ref leftController);
            Apply(right, ref rightController);
        }

        private static void SyncHand(Hand hand, bool holdsProp)
        {
            if (holdsProp)
            {
                if (hand.State != HandState.Held)
                {
                    hand.State = HandState.Held;
                    hand.Armed = false;
                    hand.SecondPress = false;
                }
            }
            else if (hand.State == HandState.Held)
            {
                // Prop left this hand by itself (thrown by the game, transferred, taken away):
                // keep the grip masked until the player lets go so nothing is re-grabbed by accident.
                hand.State = hand.WasPressed ? HandState.Masked : HandState.Idle;
            }
        }

        private static void Apply(Hand hand, ref VrControllerState controller)
        {
            bool pressed = controller.StateIsValid && controller.GripPressed;
            bool pressedThisFrame = pressed && !hand.WasPressed;
            hand.WasPressed = pressed;

            switch (hand.State)
            {
                case HandState.Held:
                    if (!pressed && !hand.Armed) hand.Armed = true;       // first let-go after the grab
                    if (pressed && hand.Armed) hand.SecondPress = true;   // second squeeze: release is pending
                    if (!pressed && hand.SecondPress)
                    {
                        // Second squeeze has been let go: now let the VR mod see the release -> it drops / throws.
                        controller.GripActive = 0;
                        hand.State = HandState.Idle;
                        hand.Armed = false;
                        hand.SecondPress = false;
                    }
                    else
                    {
                        controller.GripActive = 1; // keep holding (also through the second squeeze)
                    }
                    break;

                case HandState.Masked:
                    controller.GripActive = 0;
                    if (!pressed) hand.State = HandState.Idle;
                    break;

                default:
                    break; // Idle: physical grip passes through, so grabbing works exactly as before
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(VrGrabbing.Reset))]
        private static void ResetPostfix() => ResetState();

        private static void ResetState()
        {
            left.State = HandState.Idle; left.Armed = false; left.SecondPress = false; left.WasPressed = false;
            right.State = HandState.Idle; right.Armed = false; right.SecondPress = false; right.WasPressed = false;
        }
    }

    // ------------------------------------------------------------------
    //  Settings: extra rows in the Big Walk VR category
    // ------------------------------------------------------------------
    /// One extra OFF/ON-style row in the Big Walk VR settings category.
    internal sealed class ExtraRow
    {
        public int TypeId;            // SettingsType id (VR mod uses 101..108)
        public string Title;
        public string Label0, Label1, Label2; // valueIndex 0 / 1 / (2 when Label2 is set)
        public Func<int> Get;                 // selected index
        public Action<int> Set;
        public int Count => Label2 == null ? 2 : 3;

        public string RowName => "BigWalkVR " + Title;
    }

    internal static class ExtraRows
    {
        internal static readonly ExtraRow[] All =
        {
            new ExtraRow
            {
                TypeId = 121, Title = "AUTO SPRINT", Label0 = "OFF", Label1 = "ON",
                Get = () => ExtrasPlugin.AutoSprint.Value ? 1 : 0,
                Set = v => ExtrasPlugin.AutoSprint.Value = v != 0,
            },
            new ExtraRow
            {
                TypeId = 122, Title = "GRIP MODE", Label0 = "HOLD", Label1 = "TOGGLE",
                Get = () => ExtrasPlugin.GripToggle.Value ? 1 : 0,
                Set = v => ExtrasPlugin.GripToggle.Value = v != 0,
            },
            new ExtraRow
            {
                TypeId = 123, Title = "VIRTUAL CROUCH", Label0 = "OFF", Label1 = "HOLD", Label2 = "TOGGLE",
                Get = () => Mathf.Clamp(ExtrasPlugin.VirtualCrouchMode.Value, 0, 2),
                Set = v => ExtrasPlugin.VirtualCrouchMode.Value = v,
            },
        };

        internal static ExtraRow Def(SettingsRow row)
        {
            if (row == null) return null;
            int id = (int)row.settingsType;
            foreach (var d in All) if (d.TypeId == id) return d;
            return null;
        }

        internal static bool IsOurs(SettingsRow row) => Def(row) != null;

        internal static void Refresh(SettingsRow row)
        {
            var d = Def(row); if (d == null) return;
            int v = d.Get();
            row.SetUnderlineSingle(row.label0, v == 0);
            row.SetUnderlineSingle(row.label1, v == 1);
            if (d.Count == 3 && row.label2 != null) row.SetUnderlineSingle(row.label2, v == 2);
        }

        internal static void Set(SettingsRow row, int value)
        {
            var d = Def(row); if (d == null) return;
            value = Mathf.Clamp(value, 0, d.Count - 1);
            if (d.Get() != value)
            {
                d.Set(value);
                ExtrasPlugin.AutoSprint.ConfigFile.Save();
            }
            Refresh(row);
        }

        internal static void Cycle(SettingsRow row, int delta)
        {
            var d = Def(row); if (d == null) return;
            int n = d.Count;
            Set(row, ((d.Get() + delta) % n + n) % n);
        }

        internal static SettingsRow Find(SettingsCatagory category, ExtraRow def)
        {
            if (category == null) return null;
            foreach (var r in category.GetComponentsInChildren<SettingsRow>(true))
                if (r != null && (int)r.settingsType == def.TypeId) return r;
            return null;
        }

        /// Creates our rows inside the VR category (cloned from the TURN MODE row).
        internal static int CreateAll(SettingsCatagory category)
        {
            SettingsRow binaryTemplate = null, ternaryTemplate = null;
            foreach (var r in category.GetComponentsInChildren<SettingsRow>(true))
            {
                if ((int)r.settingsType == 101) binaryTemplate = r;   // TURN MODE  (SNAP / SMOOTH)
                if ((int)r.settingsType == 104) ternaryTemplate = r;  // DEV CAMERA (OFF / HANDHELD / FIRST PERSON)
            }
            if (binaryTemplate == null)
            {
                ExtrasPlugin.Logger.LogWarning("TURN MODE row not found; extra rows not added.");
                return 0;
            }

            int created = 0;
            foreach (var def in All)
            {
                if (Find(category, def) != null) { created++; continue; }

                var template = def.Count == 3 && ternaryTemplate != null ? ternaryTemplate : binaryTemplate;
                var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
                go.name = def.RowName;
                go.transform.localPosition = template.transform.localPosition;
                go.transform.localRotation = template.transform.localRotation;
                go.transform.localScale = template.transform.localScale;
                go.transform.SetAsLastSibling();

                var row = go.GetComponent<SettingsRow>();
                row.settingsType = (SettingsType)def.TypeId;
                VrSettingsMenu.SetText(go, "Title", def.Title);
                if (def.Count == 3)
                {
                    if (row.label2 != null) row.label2.gameObject.SetActive(true);
                    VrSettingsMenu.ConfigureTernaryModeRow(row, def.Label0, def.Label1, def.Label2);
                }
                else VrSettingsMenu.ConfigureBinaryModeRow(row, def.Label0, def.Label1);
                go.SetActive(true);
                Refresh(row);
                created++;
            }
            return created;
        }

        /// Appends our rows to the category's row list, fixes navigation and stacks them below the last visible row.
        internal static void Link(SettingsCatagory category)
        {
            var ours = new List<SettingsRow>();
            foreach (var def in All)
            {
                var r = Find(category, def);
                if (r != null) ours.Add(r);
            }
            if (ours.Count == 0) return;

            var current = category.rows;
            var list = new List<SettingsRow>();
            if (current != null)
                foreach (var r in current) if (r != null && !IsOurs(r)) list.Add(r);

            // Row spacing from the two last VR-mod rows; stack ours below them.
            if (list.Count >= 2)
            {
                var last = list[list.Count - 1].transform;
                var prev = list[list.Count - 2].transform;
                var step = last.localPosition - prev.localPosition;
                for (int i = 0; i < ours.Count; i++)
                    ours[i].transform.localPosition = last.localPosition + step * (i + 1);
            }

            list.AddRange(ours);
            category.rows = new Il2CppReferenceArray<SettingsRow>(list.ToArray());
            for (int i = 0; i < list.Count; i++)
                list[i].SetNavigation(i > 0 ? list[i - 1] : null, i < list.Count - 1 ? list[i + 1] : null);
        }
    }

    [HarmonyPatch(typeof(VrSettingsMenu))]
    internal static class SettingsMenuPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("AddVrCategory")]
        private static void AddVrCategoryPostfix(SettingsMenu menu)
        {
            try
            {
                var parent = menu.catagoryGraphics.transform.parent;
                SettingsCatagory category = null;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child.name == "BigWalkVR Settings")
                    {
                        var c = child.GetComponent<SettingsCatagory>();
                        if (c != null) category = c; // last one wins (freshly created)
                    }
                }
                if (category == null)
                {
                    ExtrasPlugin.Logger.LogWarning("BigWalkVR Settings category not found.");
                    return;
                }
                int n = ExtraRows.CreateAll(category);
                if (n > 0)
                {
                    ExtraRows.Link(category);
                    ExtrasPlugin.Logger.LogInfo($"Added {n} extra row(s) (AUTO SPRINT, GRIP MODE, VIRTUAL CROUCH) to the Big Walk VR settings.");
                }
            }
            catch (Exception e)
            {
                ExtrasPlugin.Logger.LogError("Failed to add extra settings rows: " + e);
            }
        }

        // The VR mod rebuilds the row list / navigation whenever the dev-camera mode changes.
        [HarmonyPostfix]
        [HarmonyPatch("RefreshCameraRows")]
        private static void RefreshCameraRowsPostfix(VrSettingsMenu.CameraRowSet rows)
        {
            try
            {
                var set = rows;
                if (set?.Category != null) ExtraRows.Link(set.Category);
            }
            catch (Exception e)
            {
                ExtrasPlugin.Logger.LogError("Failed to relink extra settings rows: " + e);
            }
        }
    }

    // Handle our row in the game's SettingsRow callbacks (run before the VR mod's own prefixes).
    [HarmonyPatch(typeof(SettingsRow))]
    [HarmonyPriority(Priority.High)]
    internal static class SettingsRowPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.Start))]
        private static bool Start(SettingsRow __instance)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            __instance.settingsMenu = __instance.GetComponentInParent<SettingsMenu>(true);
            ExtraRows.Refresh(__instance);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.OnEnable))]
        private static bool OnEnable(SettingsRow __instance)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            ExtraRows.Refresh(__instance);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.Refresh))]
        private static bool Refresh(SettingsRow __instance)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            ExtraRows.Refresh(__instance);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.ActionCycle))]
        private static bool ActionCycle(SettingsRow __instance, int delta)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            ExtraRows.Cycle(__instance, delta == 0 ? 1 : delta);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.ActionSelect))]
        private static bool ActionSelect(SettingsRow __instance, int valueIndex)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            ExtraRows.Set(__instance, valueIndex);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.ActionNavigate))]
        private static bool ActionNavigate(SettingsRow __instance)
        {
            return !ExtraRows.IsOurs(__instance);
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.ActionSetFromSlider))]
        private static bool ActionSetFromSlider(SettingsRow __instance)
        {
            return !ExtraRows.IsOurs(__instance);
        }

        [HarmonyPrefix, HarmonyPatch(nameof(SettingsRow.ActionBack))]
        private static bool ActionBack(SettingsRow __instance)
        {
            if (!ExtraRows.IsOurs(__instance)) return true;
            __instance.settingsMenu?.GoBackFromCatagory();
            return false;
        }
    }
}
