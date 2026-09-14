#if CORE_TIMED_NETWORK_TESTS
using System;
namespace UnityEngine {
 public struct Vector2 {}
 public static class Time {public static float time;}
 public static class Mathf {public static float Max(float a,float b)=>Math.Max(a,b); public static float Min(float a,float b)=>Math.Min(a,b);}
 public static class Debug {public static void LogWarning(object o){}}
}
namespace StarVortex { public class GameShip {public string name="Test";public bool local=true; public bool IsPlayer()=>local; public Transform transform=new Transform();} public class Transform {public UnityEngine.Vector2 position;}}
public static class CoreShieldGrant {public static float Total; public static float Grant(StarVortex.GameShip s,float a,UnityEngine.Vector2 p){Total+=a;return a;}}
class TimedEffectsTest {
 static void Equal(float actual,float expected){if(Math.Abs(actual-expected)>.001)throw new Exception(actual+" != "+expected);}
 static int Main(){
 int notices=0;
 CoreTimedShipEffects.RegisterPresentation(513,(s,d)=>{notices++;});
 var ship=new StarVortex.GameShip();var profile=CoreTimedShipEffects.Profile.Identity;
 profile.MovementMultiplier=1.2f;profile.HeatGenerationMultiplier=.7f;profile.WeaponCadenceMultiplier=1.2f;profile.ShieldGrantPerSecond=100f;
 if(!CoreTimedShipEffects.ApplyOrRefresh(ship,513,8,profile))throw new Exception("apply");
 uint firstRevision;float remaining;
 if(!CoreTimedShipEffects.TryGetPresentation(ship,513,out firstRevision,out remaining)||firstRevision==0)throw new Exception("snapshot");
 Equal(remaining,8);
 Equal(CoreTimedShipEffects.GetMovementMultiplier(ship),1.2f);Equal(CoreTimedShipEffects.GetHeatGenerationMultiplier(ship),.7f);Equal(CoreTimedShipEffects.GetWeaponCadenceMultiplier(ship),1.2f);
 UnityEngine.Time.time=4;CoreTimedShipEffects.Tick(ship,4);Equal(CoreShieldGrant.Total,400);
 CoreTimedShipEffects.ApplyOrRefresh(ship,513,8,profile);Equal(CoreTimedShipEffects.GetMovementMultiplier(ship),1.2f);
 uint refreshed;
 if(!CoreTimedShipEffects.TryGetPresentation(ship,513,out refreshed,out remaining)||refreshed==firstRevision||notices!=2)throw new Exception("refresh identity/notice");
 Equal(remaining,8);
 UnityEngine.Time.time=12.5f;CoreTimedShipEffects.Tick(ship,8.5f);Equal(CoreShieldGrant.Total,1200);
 if(CoreTimedShipEffects.TryGetPresentation(ship,513,out refreshed,out remaining))throw new Exception("expired snapshot");
 if(CoreTimedShipEffects.HasEffect(ship,513))throw new Exception("expiry");Equal(CoreTimedShipEffects.GetMovementMultiplier(ship),1);Equal(CoreTimedShipEffects.GetHeatGenerationMultiplier(ship),1);Equal(CoreTimedShipEffects.GetWeaponCadenceMultiplier(ship),1);
 if(CoreTimedShipEffects.ApplyOrRefresh(new StarVortex.GameShip{local=false},513,8,profile))throw new Exception("remote authority");
 Console.WriteLine("Timed-effect source tests passed: multipliers, shield integration, refresh without stacking, expiry clipping/removal, remote rejection. Engine clock/ship/shield are test doubles.");return 0;}}

#endif
