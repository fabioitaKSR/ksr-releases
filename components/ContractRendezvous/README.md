# KSR CS2 orbital rendezvous correction

Contract Configurator's stock `Rendezvous` factory parses `targetBody` through
its base factory but does not pass that body to the generated parameter. The
runtime parameter also accepts any nearby vessel, including a freshly decoupled
part from the same launch.

`KSRRendezvous` is scoped to a `VesselParameterGroup` and requires both vessels
to be orbiting the configured body, within the configured distance, and to have
non-empty, disjoint `Part.launchID` sets. This preserves a real rendezvous
between independently launched craft while rejecting separation from one craft.

`KSRDocking` applies the same target-body, orbital-state and independent-launch
requirements when the native part-attach event reports a docking. It is fully
event-driven and adds no timer, frame loop or vessel scan.

The parameter checks at Contract Configurator's existing default interval of
0.5 seconds. It writes no recurring log messages. Definition changes only
affect contracts generated after game restart; accepted contracts are not
migrated automatically.
