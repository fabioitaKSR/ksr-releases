using System;
using System.Collections.Generic;
using System.Reflection;
using KerbalSpaceRace.RoverTools;

namespace UnityEngine {
    public class MonoBehaviour { public bool enabled; public static void DontDestroyOnLoad(object o) {} }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Mathf { public static float Max(float a,float b){return Math.Max(a,b);} public static int CeilToInt(float x){return (int)Math.Ceiling(x);} }
    public static class Debug { public static void Log(string s) {} }
    public struct Vector3 { public static Vector3 zero; }
    public class Rigidbody { public Vector3 velocity,angularVelocity; }
}
public struct Vector3d {
    public double magnitude; public static Vector3d zero; public Vector3d normalized {get{return this;}}
    public static Vector3d operator +(Vector3d a,Vector3d b){return a;}
    public static Vector3d operator -(Vector3d a,Vector3d b){return a;}
    public static Vector3d operator *(Vector3d a,double b){return a;}
}
public class KSPAddon : Attribute {public enum Startup{MainMenu} public KSPAddon(Startup s,bool b){} }
public enum GameScenes { MAINMENU,FLIGHT,SPACECENTER,TRACKSTATION }
public static class HighLogic {public static GameScenes LoadedScene;}
public static class FlightGlobals {public static Vessel ActiveVessel; public static List<Vessel> Vessels = new List<Vessel>();}
public static class CheatOptions {public static bool NoCrashDamage,UnbreakableJoints,IgnoreMaxTemperature;}
public enum ScreenMessageStyle {UPPER_CENTER}
public static class ScreenMessages {public static void PostScreenMessage(string s,float f,ScreenMessageStyle st){} }
public class Signal {public void Add(Action a){} public void Remove(Action a){} }
public class Signal<T> {public void Add(Action<T> a){} public void Remove(Action<T> a){} }
public static class GameEvents {
    public static Signal<Vessel> onVesselChange=new Signal<Vessel>(),onVesselLoaded=new Signal<Vessel>(),onVesselGoOffRails=new Signal<Vessel>();
    public static Signal<GameScenes> onGameSceneLoadRequested=new Signal<GameScenes>(),onLevelWasLoaded=new Signal<GameScenes>();
    public static Signal onFlightReady=new Signal();
}
public class Body {public Vector3d position;}
public class Vessel {
    public enum Situations{LANDED,SPLASHED,ORBITING,SUB_ORBITAL,ESCAPING,PRELAUNCH,FLYING}
    public Situations situation=Situations.LANDED; public bool Landed=true,packed;
    public string landedAt,displaylandedAt,landedAtLast,vesselName="test";
    public float heightFromTerrain=1; public double radarAltitude=1,srfSpeed=2;
    public Vector3d rb_velocityD; public Body mainBody=new Body(); public int velocityCalls,liftCalls;
    public List<Part> parts=new List<Part>();
    public void SetWorldVelocity(Vector3d v){velocityCalls++;}
    public Vector3d GetWorldPos3D(){return new Vector3d();}
    public void SetPosition(Vector3d p){liftCalls++;}
}
public class Part {
    public uint flightID; public float crashTolerance=10,breakingForce=20,breakingTorque=30,explosionPotential=1;
    public double maxTemp=100,skinMaxTemp=100; public UnityEngine.Rigidbody rb=new UnityEngine.Rigidbody();
}
namespace KerbalSpaceRace.RoverTools {
    public class KSRRoverToolsSettings {public bool VesselLoadProtectionEnabled=true; public double VesselLoadProtectionSeconds=60;
        public static KSRRoverToolsSettings Load(){return new KSRRoverToolsSettings();}}
}
public static class Harness {
    static int passed;
    static void Assert(bool b,string message){if(!b)throw new Exception(message);}
    static void Call(KSRVesselLoadProtection p,string method,params object[] args){typeof(KSRVesselLoadProtection).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(p,args);}
    static Vessel NewVessel(Vessel.Situations situation){var v=new Vessel{ situation=situation,Landed=situation==Vessel.Situations.LANDED};v.parts.Add(new Part{flightID=(uint)(FlightGlobals.Vessels.Count+1)});FlightGlobals.Vessels.Add(v);return v;}
    static KSRVesselLoadProtection Setup(out Vessel rover){FlightGlobals.Vessels.Clear();UnityEngine.Time.realtimeSinceStartup=0;HighLogic.LoadedScene=GameScenes.FLIGHT;CheatOptions.NoCrashDamage=false;CheatOptions.UnbreakableJoints=true;CheatOptions.IgnoreMaxTemperature=false;rover=NewVessel(Vessel.Situations.LANDED);FlightGlobals.ActiveVessel=rover;var p=new KSRVesselLoadProtection();p.Awake();Call(p,"OnFlightReady");Assert(rover.velocityCalls==1,"rover protection did not start");return p;}
    static void Restored(Vessel rover){Assert(!CheatOptions.NoCrashDamage&&CheatOptions.UnbreakableJoints&&!CheatOptions.IgnoreMaxTemperature,"previous cheats not restored");Assert(rover.parts[0].crashTolerance==10&&rover.parts[0].maxTemp==100&&rover.parts[0].explosionPotential==1,"part durability not restored");}
    static void Test(string name,Action test){test();passed++;Console.WriteLine("PASS "+name);}
    public static int Main(){try {
        Test("FLIGHT-to-FLIGHT cannot rearm outgoing rover",delegate{Vessel r;var p=Setup(out r);int calls=r.velocityCalls;Call(p,"OnGameSceneLoadRequested",GameScenes.FLIGHT);p.Update();p.FixedUpdate();Call(p,"OnVesselLoaded",r);Call(p,"OnSceneLoaded",GameScenes.FLIGHT);p.Update();Assert(r.velocityCalls==calls,"old rover rearmed");Restored(r);var o=NewVessel(Vessel.Situations.ORBITING);o.srfSpeed=305;FlightGlobals.ActiveVessel=o;Call(p,"OnVesselChanged",o);Call(p,"OnFlightReady");Call(p,"OnVesselGoOffRails",o);p.FixedUpdate();Assert(o.velocityCalls==0&&o.liftCalls==0,"orbital vessel modified");});
        Test("expired rover cannot rearm during transition",delegate{Vessel r;var p=Setup(out r);UnityEngine.Time.realtimeSinceStartup=61;p.Update();Restored(r);int calls=r.velocityCalls;Call(p,"OnGameSceneLoadRequested",GameScenes.FLIGHT);p.Update();p.FixedUpdate();Assert(r.velocityCalls==calls,"expired rover rearmed");});
        Test("vessel change restores protection before orbit",delegate{Vessel r;var p=Setup(out r);var o=NewVessel(Vessel.Situations.ORBITING);FlightGlobals.ActiveVessel=o;Call(p,"OnVesselChanged",o);p.FixedUpdate();Restored(r);Assert(o.velocityCalls==0,"orbital velocity modified");});
        Test("physics tick catches switch before events",delegate{Vessel r;var p=Setup(out r);var o=NewVessel(Vessel.Situations.ORBITING);FlightGlobals.ActiveVessel=o;p.FixedUpdate();Restored(r);Assert(o.velocityCalls==0,"physics race modifies orbit");});
        Test("surface rover switches retain separate snapshots",delegate{Vessel r;var p=Setup(out r);var second=NewVessel(Vessel.Situations.LANDED);FlightGlobals.ActiveVessel=second;Call(p,"OnVesselChanged",second);Assert(r.parts[0].crashTolerance==10,"old rover durability leaked");Assert(second.velocityCalls==1&&second.parts[0].crashTolerance==100000,"new rover not protected");UnityEngine.Time.realtimeSinceStartup=61;p.FixedUpdate();Restored(second);});
        Test("nonactive load cannot redirect or extend timer",delegate{Vessel r;var p=Setup(out r);var other=NewVessel(Vessel.Situations.LANDED);UnityEngine.Time.realtimeSinceStartup=50;Call(p,"OnVesselLoaded",other);Assert(other.velocityCalls==0&&other.parts[0].crashTolerance==10,"background vessel modified");UnityEngine.Time.realtimeSinceStartup=60;p.FixedUpdate();Restored(r);});
        Test("expiry enforced in physics without Update",delegate{Vessel r;var p=Setup(out r);int calls=r.velocityCalls;UnityEngine.Time.realtimeSinceStartup=60;p.FixedUpdate();Assert(r.velocityCalls==calls,"expired physics intervention");Restored(r);});
        Test("orbital situation overrides stale landed flag",delegate{Vessel r;var p=Setup(out r);int calls=r.velocityCalls;r.situation=Vessel.Situations.ORBITING;p.FixedUpdate();Assert(r.velocityCalls==calls,"stale Landed allowed orbit");Restored(r);});
        Test("leaving surface terminates active protection",delegate{Vessel r;var p=Setup(out r);r.Landed=false;r.situation=Vessel.Situations.FLYING;r.heightFromTerrain=300;r.radarAltitude=300;int calls=r.velocityCalls;p.FixedUpdate();Assert(r.velocityCalls==calls,"airborne vessel modified");Restored(r);});
        Test("tracking scene restores immediately",delegate{Vessel r;var p=Setup(out r);Call(p,"OnGameSceneLoadRequested",GameScenes.TRACKSTATION);Restored(r);p.FixedUpdate();});
        Test("null active vessel restores immediately",delegate{Vessel r;var p=Setup(out r);FlightGlobals.ActiveVessel=null;p.FixedUpdate();Restored(r);});
        Test("duplicate load callbacks do not re-lift",delegate{Vessel r;var p=Setup(out r);int lifts=r.liftCalls;UnityEngine.Time.realtimeSinceStartup=50;Call(p,"OnVesselLoaded",r);Call(p,"OnVesselChanged",r);Call(p,"OnFlightReady");Assert(r.liftCalls==lifts,"duplicate callbacks re-lifted rover");UnityEngine.Time.realtimeSinceStartup=60;p.FixedUpdate();Restored(r);});
        Test("unpack still protects and stabilizes rover",delegate{Vessel r;var p=Setup(out r);r.packed=true;int calls=r.velocityCalls;p.FixedUpdate();Assert(r.velocityCalls==calls,"packed vessel modified");r.packed=false;UnityEngine.Time.realtimeSinceStartup=10;Call(p,"OnVesselGoOffRails",r);Assert(r.velocityCalls==calls+1,"unpack protection missing");UnityEngine.Time.realtimeSinceStartup=70;p.FixedUpdate();Restored(r);});
        Test("launchpad excluded",delegate{Vessel r;var p=Setup(out r);var pad=NewVessel(Vessel.Situations.PRELAUNCH);FlightGlobals.ActiveVessel=pad;Call(p,"OnVesselChanged",pad);p.FixedUpdate();Assert(pad.velocityCalls==0,"prelaunch modified");Restored(r);});
        Test("destruction restores previous state",delegate{Vessel r;var p=Setup(out r);p.OnDestroy();Restored(r);});
        Console.WriteLine(passed+" regression tests passed.");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
