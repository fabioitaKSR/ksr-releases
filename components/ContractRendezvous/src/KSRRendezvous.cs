using System.Collections.Generic;
using System.Linq;
using ContractConfigurator;
using ContractConfigurator.Parameters;
using Contracts;
using UnityEngine;

namespace KerbalSpaceRace.ContractRendezvous
{
    public static class KSRRendezvousRules
    {
        public static bool HaveIndependentOrigins(
            IEnumerable<uint> firstLaunchIds,
            IEnumerable<uint> secondLaunchIds)
        {
            var first = new HashSet<uint>(firstLaunchIds.Where(id => id != 0));
            var second = new HashSet<uint>(secondLaunchIds.Where(id => id != 0));

            return first.Count > 0 && second.Count > 0 && !first.Overlaps(second);
        }

        public static bool MeetsDockingConditions(
            bool bothAtTargetBody,
            bool firstIsOrbiting,
            bool secondIsOrbiting,
            IEnumerable<uint> firstLaunchIds,
            IEnumerable<uint> secondLaunchIds)
        {
            return bothAtTargetBody && firstIsOrbiting && secondIsOrbiting &&
                HaveIndependentOrigins(firstLaunchIds, secondLaunchIds);
        }

        public static bool MeetsConditions(
            bool bothAtTargetBody,
            bool firstIsOrbiting,
            bool secondIsOrbiting,
            IEnumerable<uint> firstLaunchIds,
            IEnumerable<uint> secondLaunchIds,
            double distance,
            double maximumDistance)
        {
            if (!bothAtTargetBody || !firstIsOrbiting || !secondIsOrbiting)
            {
                return false;
            }

            if (distance >= maximumDistance)
            {
                return false;
            }

            // A rendezvous must involve two independently launched craft. Requiring
            // disjoint launch lineages rejects freshly decoupled parts of one craft.
            return HaveIndependentOrigins(firstLaunchIds, secondLaunchIds);
        }
    }

    public sealed class KSRRendezvousFactory : ParameterFactory
    {
        private double distance;
        private float updateFrequency;

        public override bool Load(ConfigNode configNode)
        {
            var valid = base.Load(configNode);
            if (!(parent is VesselParameterGroupFactory))
            {
                LoggingUtil.LogError(this,
                    "{0}: KSRRendezvous must be inside a VesselParameterGroup.",
                    ErrorPrefix());
                valid = false;
            }

            valid &= ConfigNodeUtil.ParseValue(
                configNode, "distance", value => distance = value, this, 2000.0,
                value => Validation.GT(value, 0.0));
            valid &= ConfigNodeUtil.ParseValue(
                configNode, "updateFrequency", value => updateFrequency = value,
                this, 0.5f, value => Validation.GT(value, 0.0f));
            return valid;
        }

        public override ContractParameter Generate(Contract contract)
        {
            return new KSRRendezvous(targetBody, distance, title, updateFrequency);
        }
    }

    public sealed class KSRRendezvous : VesselParameter
    {
        private CelestialBody requiredBody;
        private double distance;
        private float updateFrequency;
        private float lastUpdate;

        public KSRRendezvous() : base(null)
        {
        }

        public KSRRendezvous(
            CelestialBody requiredBody,
            double distance,
            string title,
            float updateFrequency) : base(title)
        {
            this.requiredBody = requiredBody;
            this.distance = distance;
            this.updateFrequency = updateFrequency;
            disableOnStateChange = true;
        }

        protected override string GetParameterTitle()
        {
            return string.IsNullOrEmpty(title)
                ? "Esegui un rendezvous orbitale tra veicoli di lanci differenti"
                : title;
        }

        protected override void OnParameterSave(ConfigNode node)
        {
            base.OnParameterSave(node);
            node.AddValue("distance", distance);
            node.AddValue("updateFrequency", updateFrequency);
            if (requiredBody != null)
            {
                node.AddValue("targetBody", requiredBody.name);
            }
        }

        protected override void OnParameterLoad(ConfigNode node)
        {
            base.OnParameterLoad(node);
            distance = ConfigNodeUtil.ParseValue<double>(node, "distance");
            updateFrequency = ConfigNodeUtil.ParseValue(node, "updateFrequency", 0.5f);
            requiredBody = ConfigNodeUtil.ParseValue<CelestialBody>(node, "targetBody");
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            if (Time.fixedTime - lastUpdate <= updateFrequency)
            {
                return;
            }

            lastUpdate = Time.fixedTime;
            var group = Parent as VesselParameterGroup;
            var primary = group != null
                ? group.TrackedVessel ?? FlightGlobals.ActiveVessel
                : FlightGlobals.ActiveVessel;

            if (primary == null || IsIgnoredVesselType(primary.vesselType))
            {
                return;
            }

            var matched = false;
            foreach (var candidate in FlightGlobals.Vessels)
            {
                if (candidate == null || candidate == primary ||
                    IsIgnoredVesselType(candidate.vesselType))
                {
                    continue;
                }

                var separation = Vector3.Distance(
                    primary.transform.position, candidate.transform.position);
                if (KSRRendezvousRules.MeetsConditions(
                    primary.mainBody == requiredBody && candidate.mainBody == requiredBody,
                    primary.situation == Vessel.Situations.ORBITING,
                    candidate.situation == Vessel.Situations.ORBITING,
                    LaunchIds(primary), LaunchIds(candidate), separation, distance))
                {
                    matched = true;
                    break;
                }
            }

            var changed = SetState(
                primary, matched ? ParameterState.Complete : ParameterState.Incomplete);
            CheckVessel(primary, changed);
        }

        protected override bool VesselMeetsCondition(Vessel vessel)
        {
            return GetState(vessel) == ParameterState.Complete;
        }

        internal static IEnumerable<uint> LaunchIds(Vessel vessel)
        {
            return vessel.parts
                .Where(part => part != null && part.launchID != 0)
                .Select(part => part.launchID)
                .Distinct();
        }
    }

    public sealed class KSRDockingFactory : ParameterFactory
    {
        public override bool Load(ConfigNode configNode)
        {
            var valid = base.Load(configNode);
            if (!(parent is VesselParameterGroupFactory))
            {
                LoggingUtil.LogError(this,
                    "{0}: KSRDocking must be inside a VesselParameterGroup.",
                    ErrorPrefix());
                valid = false;
            }

            return valid;
        }

        public override ContractParameter Generate(Contract contract)
        {
            return new KSRDocking(targetBody, title);
        }
    }

    public sealed class KSRDocking : VesselParameter
    {
        private CelestialBody requiredBody;

        public KSRDocking() : base(null)
        {
        }

        public KSRDocking(CelestialBody requiredBody, string title) : base(title)
        {
            this.requiredBody = requiredBody;
            disableOnStateChange = true;
        }

        protected override string GetParameterTitle()
        {
            return string.IsNullOrEmpty(title)
                ? "Esegui un docking orbitale con un veicolo di un altro lancio"
                : title;
        }

        protected override void OnParameterSave(ConfigNode node)
        {
            base.OnParameterSave(node);
            if (requiredBody != null)
            {
                node.AddValue("targetBody", requiredBody.name);
            }
        }

        protected override void OnParameterLoad(ConfigNode node)
        {
            base.OnParameterLoad(node);
            requiredBody = ConfigNodeUtil.ParseValue<CelestialBody>(node, "targetBody");
        }

        protected override void OnPartAttach(GameEvents.HostTargetAction<Part, Part> action)
        {
            base.OnPartAttach(action);
            if (HighLogic.LoadedScene == GameScenes.LOADING ||
                action.host == null || action.target == null)
            {
                return;
            }

            var first = action.host.vessel;
            var second = action.target.vessel;
            if (first == null || second == null || first == second)
            {
                return;
            }

            var group = Parent as VesselParameterGroup;
            var primary = group != null
                ? group.TrackedVessel ?? FlightGlobals.ActiveVessel
                : FlightGlobals.ActiveVessel;
            if (primary == null || (first != primary && second != primary))
            {
                return;
            }

            if (!KSRRendezvousRules.MeetsDockingConditions(
                first.mainBody == requiredBody && second.mainBody == requiredBody,
                first.situation == Vessel.Situations.ORBITING,
                second.situation == Vessel.Situations.ORBITING,
                KSRRendezvous.LaunchIds(first), KSRRendezvous.LaunchIds(second)))
            {
                return;
            }

            var changed = SetState(first, ParameterState.Complete);
            changed |= SetState(second, ParameterState.Complete);
            CheckVessel(FlightGlobals.ActiveVessel, changed);
        }

        protected override bool VesselMeetsCondition(Vessel vessel)
        {
            return GetState(vessel) == ParameterState.Complete;
        }
    }
}
