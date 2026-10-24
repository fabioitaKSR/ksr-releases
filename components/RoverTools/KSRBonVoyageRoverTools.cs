using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace KerbalSpaceRace.RoverTools
{
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public sealed class KSRVesselLoadProtection : MonoBehaviour
    {
        private KSRRoverToolsSettings settings;
        private Vessel protectedVessel;
        private Vessel observedVessel;
        private float vesselProtectionUntil;
        private bool vesselProtectionActive;
        private bool previousNoCrashDamage;
        private bool previousUnbreakableJoints;
        private bool previousIgnoreMaxTemperature;
        private int lastCountdown = -1;
        private bool flightReady;
        private float lastSpeedInterventionLogTime;
        private readonly Dictionary<uint, PartDurability> protectedParts = new Dictionary<uint, PartDurability>();

        public void Awake()
        {
            DontDestroyOnLoad(this);
            settings = KSRRoverToolsSettings.Load();
            if (!settings.VesselLoadProtectionEnabled)
            {
                enabled = false;
                return;
            }

            GameEvents.onVesselChange.Add(OnVesselChanged);
            GameEvents.onVesselLoaded.Add(OnVesselLoaded);
            GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
            GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Add(OnSceneLoaded);
            GameEvents.onFlightReady.Add(OnFlightReady);
            Debug.Log("[KSRVesselLoadProtection] Enabled. Seconds=" + settings.VesselLoadProtectionSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        }

        public void OnDestroy()
        {
            try
            {
                GameEvents.onVesselChange.Remove(OnVesselChanged);
                GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
                GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
                GameEvents.onGameSceneLoadRequested.Remove(OnGameSceneLoadRequested);
                GameEvents.onLevelWasLoaded.Remove(OnSceneLoaded);
                GameEvents.onFlightReady.Remove(OnFlightReady);
                EndProtection(true);
            }
            catch
            {
            }
        }

        public void Update()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                EndProtection(true);
                observedVessel = null;
                flightReady = false;
                return;
            }

            // ActiveVessel may still refer to the outgoing rover during FLIGHT -> FLIGHT.
            if (!flightReady)
            {
                return;
            }

            if (FlightGlobals.ActiveVessel == null)
            {
                EndProtection(true);
                observedVessel = null;
                return;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != observedVessel)
            {
                StartProtection(activeVessel, "active vessel load");
            }

            if (vesselProtectionActive && !CanStabilize(activeVessel))
            {
                EndProtection(true);
                return;
            }

            UpdateCountdownMessage();
        }

        public void FixedUpdate()
        {
            if (!vesselProtectionActive)
            {
                return;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (!CanStabilize(activeVessel))
            {
                EndProtection(true);
                return;
            }

            StabilizeVessel(activeVessel, false);
        }

        private void OnVesselChanged(Vessel changedVessel)
        {
            StartProtection(changedVessel, "vessel change");
        }

        private void OnVesselLoaded(Vessel loadedVessel)
        {
            if (loadedVessel == FlightGlobals.ActiveVessel)
            {
                StartProtection(loadedVessel, "vessel loaded");
            }
        }

        private void OnVesselGoOffRails(Vessel unpackedVessel)
        {
            if (vesselProtectionActive && unpackedVessel == FlightGlobals.ActiveVessel)
            {
                StartProtection(unpackedVessel, "active vessel unpack");
            }
        }

        private void OnGameSceneLoadRequested(GameScenes scene)
        {
            EndProtection(true);
            flightReady = false;
            observedVessel = null;
        }

        private void OnSceneLoaded(GameScenes scene)
        {
            EndProtection(true);
            flightReady = false;
            observedVessel = null;
        }

        private void OnFlightReady()
        {
            flightReady = HighLogic.LoadedScene == GameScenes.FLIGHT;
            StartProtection(FlightGlobals.ActiveVessel, "flight ready");
        }

        private void StartProtection(Vessel targetVessel, string reason)
        {
            if (!flightReady || HighLogic.LoadedScene != GameScenes.FLIGHT ||
                targetVessel == null || targetVessel != FlightGlobals.ActiveVessel)
            {
                return;
            }

            if (targetVessel != protectedVessel)
            {
                EndProtection(true);
            }
            observedVessel = targetVessel;
            if (!IsSurfaceLoadCandidate(targetVessel))
            {
                EndProtection(true);
                Debug.Log("[KSRVesselLoadProtection] Skipped protection for " + (targetVessel == null ? "(no active vessel)" : targetVessel.vesselName) + " (" + reason + "): not landed/near surface.");
                return;
            }

            if (vesselProtectionActive && reason != "active vessel unpack")
            {
                return;
            }
            protectedVessel = targetVessel;

            float duration = Mathf.Max(0.1f, (float)settings.VesselLoadProtectionSeconds);
            vesselProtectionUntil = Time.realtimeSinceStartup + duration;
            if (!vesselProtectionActive)
            {
                previousNoCrashDamage = CheatOptions.NoCrashDamage;
                previousUnbreakableJoints = CheatOptions.UnbreakableJoints;
                previousIgnoreMaxTemperature = CheatOptions.IgnoreMaxTemperature;
                vesselProtectionActive = true;
            }

            CheatOptions.NoCrashDamage = true;
            CheatOptions.UnbreakableJoints = true;
            CheatOptions.IgnoreMaxTemperature = true;
            if (targetVessel != null)
            {
                ProtectVesselParts(targetVessel);
                StabilizeVessel(targetVessel, true);
            }

            lastCountdown = -1;
            UpdateCountdownMessage();
            string vesselName = targetVessel == null ? "(no active vessel yet)" : targetVessel.vesselName;
            Debug.Log("[KSRVesselLoadProtection] Protection enabled for " + vesselName + " (" + reason + ").");
        }

        private bool CanStabilize(Vessel targetVessel)
        {
            return vesselProtectionActive && flightReady &&
                HighLogic.LoadedScene == GameScenes.FLIGHT &&
                targetVessel != null && targetVessel == protectedVessel &&
                targetVessel == FlightGlobals.ActiveVessel &&
                Time.realtimeSinceStartup < vesselProtectionUntil &&
                IsSurfaceLoadCandidate(targetVessel);
        }

        private void UpdateCountdownMessage()
        {
            if (!vesselProtectionActive)
            {
                return;
            }

            int remaining = Mathf.CeilToInt(Mathf.Max(0f, vesselProtectionUntil - Time.realtimeSinceStartup));
            if (remaining == lastCountdown)
            {
                return;
            }

            lastCountdown = remaining;
            ScreenMessages.PostScreenMessage("Vessel protection: " + remaining.ToString(CultureInfo.InvariantCulture), 1.05f, ScreenMessageStyle.UPPER_CENTER);
        }

        private void EndProtection(bool force)
        {
            if (!vesselProtectionActive)
            {
                return;
            }

            if (!force && Time.realtimeSinceStartup < vesselProtectionUntil)
            {
                return;
            }

            CheatOptions.NoCrashDamage = previousNoCrashDamage;
            CheatOptions.UnbreakableJoints = previousUnbreakableJoints;
            CheatOptions.IgnoreMaxTemperature = previousIgnoreMaxTemperature;
            RestorePartDurability();
            vesselProtectionActive = false;
            protectedVessel = null;
            lastCountdown = -1;
            ScreenMessages.PostScreenMessage("Vessel protection ended", 2f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[KSRVesselLoadProtection] Restored previous cheat state.");
        }

        private static bool IsSurfaceLoadCandidate(Vessel targetVessel)
        {
            if (targetVessel == null)
            {
                return false;
            }

            if (IsLaunchSite(targetVessel))
            {
                return false;
            }

            if (targetVessel.situation == Vessel.Situations.ORBITING ||
                targetVessel.situation == Vessel.Situations.SUB_ORBITAL ||
                targetVessel.situation == Vessel.Situations.ESCAPING ||
                targetVessel.situation == Vessel.Situations.PRELAUNCH)
            {
                return false;
            }

            if (targetVessel.Landed || targetVessel.situation == Vessel.Situations.LANDED || targetVessel.situation == Vessel.Situations.SPLASHED)
            {
                return true;
            }

            double altitude = SurfaceAltitude(targetVessel);
            return altitude >= 0d && altitude <= 200d;
        }

        private static bool IsLaunchSite(Vessel targetVessel)
        {
            if (targetVessel.situation == Vessel.Situations.PRELAUNCH)
            {
                return true;
            }

            return ContainsLaunchSiteName(targetVessel.landedAt) ||
                ContainsLaunchSiteName(targetVessel.displaylandedAt) ||
                ContainsLaunchSiteName(targetVessel.landedAtLast);
        }

        private static bool ContainsLaunchSiteName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string lower = value.ToLowerInvariant();
            return lower.Contains("launchpad") ||
                lower.Contains("launch pad") ||
                lower.Contains("runway") ||
                lower.Contains("rampa") ||
                lower.Contains("pista");
        }

        private static double SurfaceAltitude(Vessel targetVessel)
        {
            double terrainHeight = targetVessel.heightFromTerrain;
            if (!double.IsNaN(terrainHeight) && terrainHeight >= 0d)
            {
                return terrainHeight;
            }

            double radarAltitude = targetVessel.radarAltitude;
            if (!double.IsNaN(radarAltitude) && radarAltitude >= 0d)
            {
                return radarAltitude;
            }

            return -1d;
        }

        private void StabilizeVessel(Vessel targetVessel, bool liftIfLow)
        {
            if (!CanStabilize(targetVessel) || targetVessel.packed)
            {
                return;
            }

            double speed = targetVessel.srfSpeed;
            if (double.IsNaN(speed))
            {
                speed = targetVessel.rb_velocityD.magnitude;
            }

            if (speed > 1d)
            {
                targetVessel.SetWorldVelocity(Vector3d.zero);
                StopVesselRigidbodies(targetVessel);
                if (Time.realtimeSinceStartup - lastSpeedInterventionLogTime > 1f)
                {
                    lastSpeedInterventionLogTime = Time.realtimeSinceStartup;
                    Debug.Log("[KSRVesselLoadProtection] Speed intervention on " + targetVessel.vesselName + ": " + speed.ToString("0.00", CultureInfo.InvariantCulture) + "m/s.");
                }
            }

            if (!liftIfLow || targetVessel.mainBody == null)
            {
                return;
            }

            try
            {
                float height = targetVessel.heightFromTerrain;
                if (float.IsNaN(height) || height < 0f)
                {
                    height = (float)targetVessel.radarAltitude;
                }

                if (height < 2.5f)
                {
                    Vector3d worldPosition = targetVessel.GetWorldPos3D();
                    Vector3d up = (worldPosition - targetVessel.mainBody.position).normalized;
                    double lift = Math.Max(0.5d, 2.5d - height);
                    targetVessel.SetPosition(worldPosition + up * lift);
                    Debug.Log("[KSRVesselLoadProtection] Lifted vessel " + targetVessel.vesselName + " by " + lift.ToString("0.00", CultureInfo.InvariantCulture) + "m during protection.");
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KSRVesselLoadProtection] Stabilize lift failed: " + ex.Message);
            }
        }

        private void ProtectVesselParts(Vessel targetVessel)
        {
            if (targetVessel == null || targetVessel.parts == null)
            {
                return;
            }

            for (int i = 0; i < targetVessel.parts.Count; i++)
            {
                Part part = targetVessel.parts[i];
                if (part == null)
                {
                    continue;
                }

                uint id = part.flightID;
                if (!protectedParts.ContainsKey(id))
                {
                    protectedParts[id] = new PartDurability(part);
                }

                part.crashTolerance = Mathf.Max(part.crashTolerance, 100000f);
                part.breakingForce = Mathf.Max(part.breakingForce, 100000f);
                part.breakingTorque = Mathf.Max(part.breakingTorque, 100000f);
                part.maxTemp = Math.Max(part.maxTemp, 100000d);
                part.skinMaxTemp = Math.Max(part.skinMaxTemp, 100000d);
                part.explosionPotential = 0f;
            }
        }

        private void RestorePartDurability()
        {
            if (protectedParts.Count == 0)
            {
                return;
            }

            foreach (Vessel loadedVessel in FlightGlobals.Vessels)
            {
                if (loadedVessel == null || loadedVessel.parts == null)
                {
                    continue;
                }

                for (int i = 0; i < loadedVessel.parts.Count; i++)
                {
                    Part part = loadedVessel.parts[i];
                    PartDurability durability;
                    if (part == null || !protectedParts.TryGetValue(part.flightID, out durability))
                    {
                        continue;
                    }

                    durability.Restore(part);
                }
            }

            protectedParts.Clear();
        }

        private static void StopVesselRigidbodies(Vessel targetVessel)
        {
            if (targetVessel == null || targetVessel.parts == null)
            {
                return;
            }

            for (int i = 0; i < targetVessel.parts.Count; i++)
            {
                Part part = targetVessel.parts[i];
                if (part == null || part.rb == null)
                {
                    continue;
                }

                part.rb.velocity = Vector3.zero;
                part.rb.angularVelocity = Vector3.zero;
            }
        }

        private struct PartDurability
        {
            private readonly float crashTolerance;
            private readonly float breakingForce;
            private readonly float breakingTorque;
            private readonly double maxTemp;
            private readonly double skinMaxTemp;
            private readonly float explosionPotential;

            public PartDurability(Part part)
            {
                crashTolerance = part.crashTolerance;
                breakingForce = part.breakingForce;
                breakingTorque = part.breakingTorque;
                maxTemp = part.maxTemp;
                skinMaxTemp = part.skinMaxTemp;
                explosionPotential = part.explosionPotential;
            }

            public void Restore(Part part)
            {
                part.crashTolerance = crashTolerance;
                part.breakingForce = breakingForce;
                part.breakingTorque = breakingTorque;
                part.maxTemp = maxTemp;
                part.skinMaxTemp = skinMaxTemp;
                part.explosionPotential = explosionPotential;
            }
        }
    }

    public sealed class KSRBonVoyageRoverTools : PartModule
    {
        [KSPEvent(guiActive = true, guiActiveEditor = false, guiName = "Raddrizza rover", active = true)]
        public void RightRover()
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || vessel == null || activeVessel == null || vessel != activeVessel)
            {
                ScreenMessages.PostScreenMessage("Raddrizza rover: seleziona il rover attivo", 2.5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            if (!HasBonVoyageModule(part))
            {
                ScreenMessages.PostScreenMessage("Serve modulo Bon Voyage", 2.5f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            try
            {
                if (vessel.packed)
                {
                    vessel.GoOffRails();
                }

                Vector3d worldPosition = vessel.GetWorldPos3D();
                Vector3 up = ((Vector3)(worldPosition - vessel.mainBody.position)).normalized;
                Vector3 forward = Vector3.ProjectOnPlane(vessel.transform.forward, up);
                if (forward.sqrMagnitude < 0.01f)
                {
                    forward = Vector3.ProjectOnPlane(vessel.transform.up, up);
                }

                if (forward.sqrMagnitude < 0.01f)
                {
                    forward = Vector3.Cross(up, Vector3.forward);
                }

                if (forward.sqrMagnitude < 0.01f)
                {
                    forward = Vector3.Cross(up, Vector3.right);
                }

                Quaternion uprightRotation = Quaternion.LookRotation(forward.normalized, up);
                vessel.SetRotation(uprightRotation);
                vessel.SetPosition(worldPosition + (Vector3d)(up * 1.75f));
                vessel.SetWorldVelocity(Vector3d.zero);
                StopVesselRigidbodies(vessel);

                ScreenMessages.PostScreenMessage("Rover raddrizzato", 2.5f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("[KSRBonVoyageRoverTools] Rover righted: " + vessel.vesselName);
            }
            catch (Exception ex)
            {
                ScreenMessages.PostScreenMessage("Raddrizzamento fallito", 2.5f, ScreenMessageStyle.UPPER_CENTER);
                Debug.Log("[KSRBonVoyageRoverTools] Rover righting failed: " + ex.Message);
            }
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            UpdateEventVisibility();
        }

        public void Update()
        {
            UpdateEventVisibility();
        }

        private void UpdateEventVisibility()
        {
            BaseEvent rightEvent = Events["RightRover"];
            if (rightEvent == null)
            {
                return;
            }

            rightEvent.active = HighLogic.LoadedScene == GameScenes.FLIGHT && vessel != null && vessel == FlightGlobals.ActiveVessel && HasBonVoyageModule(part);
        }

        private static void StopVesselRigidbodies(Vessel targetVessel)
        {
            if (targetVessel == null || targetVessel.parts == null)
            {
                return;
            }

            for (int i = 0; i < targetVessel.parts.Count; i++)
            {
                Part targetPart = targetVessel.parts[i];
                if (targetPart == null || targetPart.rb == null)
                {
                    continue;
                }

                targetPart.rb.velocity = Vector3.zero;
                targetPart.rb.angularVelocity = Vector3.zero;
            }
        }

        private static bool HasBonVoyageModule(Part targetPart)
        {
            if (targetPart == null || targetPart.Modules == null)
            {
                return false;
            }

            for (int i = 0; i < targetPart.Modules.Count; i++)
            {
                PartModule module = targetPart.Modules[i];
                if (module == null)
                {
                    continue;
                }

                string typeName = module.GetType().Name;
                if (string.Equals(module.moduleName, "BonVoyageModule", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeName, "BonVoyageModule", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class KSRRoverToolsSettings
    {
        public bool VesselLoadProtectionEnabled = true;
        public double VesselLoadProtectionSeconds = 60d;
        private readonly string configPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/KSRLite/PluginData/KSR_RoverTools.cfg");

        public static KSRRoverToolsSettings Load()
        {
            KSRRoverToolsSettings settings = new KSRRoverToolsSettings();
            ConfigNode root = File.Exists(settings.configPath) ? ConfigNode.Load(settings.configPath) : null;
            ConfigNode node = root != null ? root.GetNode("KSR_ROVER_TOOLS") : null;
            if (node == null)
            {
                settings.Save();
                return settings;
            }

            settings.VesselLoadProtectionEnabled = ReadBool(node, "vesselLoadProtectionEnabled", settings.VesselLoadProtectionEnabled);
            settings.VesselLoadProtectionSeconds = ReadDouble(node, "vesselLoadProtectionSeconds", settings.VesselLoadProtectionSeconds);
            return settings;
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                ConfigNode root = new ConfigNode();
                ConfigNode node = root.AddNode("KSR_ROVER_TOOLS");
                node.AddValue("vesselLoadProtectionEnabled", VesselLoadProtectionEnabled.ToString().ToLowerInvariant());
                node.AddValue("vesselLoadProtectionSeconds", VesselLoadProtectionSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                root.Save(configPath);
            }
            catch (Exception ex)
            {
                Debug.Log("[KSRRoverToolsSettings] Cannot save default config: " + ex.Message);
            }
        }

        private static bool ReadBool(ConfigNode node, string key, bool fallback)
        {
            bool value;
            return node.HasValue(key) && bool.TryParse(node.GetValue(key), out value) ? value : fallback;
        }

        private static double ReadDouble(ConfigNode node, string key, double fallback)
        {
            double value;
            return node.HasValue(key) && double.TryParse(node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}
