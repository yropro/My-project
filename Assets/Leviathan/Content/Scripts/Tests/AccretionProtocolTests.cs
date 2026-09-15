#if ACCRETION_PROTOCOL_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using StarVortex;
using UnityEngine;

/// <summary>
/// Runs the PRODUCTION reservoir, capture transaction service, spawn guard and
/// application observation in three isolated static runtimes. Only engine objects,
/// native despawn, clocks and the already-tested reliable grant transport are
/// doubles. Packet queues can be delayed, duplicated and dropped explicitly.
/// </summary>
public static class AccretionProtocolTests
{
    private static int checks;
    private static void Check(bool condition, string label)
    { checks++; if (!condition) throw new Exception(label); }
    private static void Near(float actual, float expected, string label)
    { Check(Math.Abs(actual - expected) < 0.001f, label + ": " + actual + " != " + expected); }
    private sealed class Node : IDisposable
    {
        private readonly AssemblyLoadContext context = new AssemblyLoadContext(Guid.NewGuid().ToString(), true);
        private readonly Type endpoint;
        public Node(int peer)
        {
            endpoint = context.LoadFromAssemblyPath(typeof(AccretionProtocolTests).Assembly.Location)
                .GetType(nameof(AccretionProtocolEndpoint));
            Call("Init", peer);
        }
        public object Call(string method, params object[] args)
        {
            try { return endpoint.GetMethod(method).Invoke(null, args); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }
        public float Value(string name) { return (float)Call("Value", name); }
        public uint[][] Drain() { return (uint[][])Call("Drain"); }
        public void Receive(uint[] message) { Check((bool)Call("Receive", (object)message), "Valid message rejected"); }
        public void Tick(float now) { Call("Tick", now); }
        public uint Spawn(float value, float x = 0f) { return (uint)Call("Spawn", value, x); }
        public bool Sweep(uint id, float distance = 20f) { return (bool)Call("Sweep", id, distance); }
        public bool Captured(uint id) { return (bool)Call("Captured", id); }
        public bool Held(uint id) { return (bool)Call("Held", id); }
        public void Field(uint gen, float capacity, float seconds = 100f) { Call("Field", gen, capacity, seconds); }
        public void Dispose() { context.Unload(); }
    }
    private sealed class Peers : IDisposable
    {
        public readonly Node[] Nodes = { new Node(0), new Node(1), new Node(2) };
        public void Advance(float now) { foreach (Node n in Nodes) n.Tick(now); }
        public void Pump(Func<uint[], bool> deliver = null)
        {
            for (int pass = 0; pass < 30; pass++)
            {
                bool work = false;
                foreach (Node n in Nodes) foreach (uint[] message in n.Drain())
                {
                    work = true;
                    if (deliver == null || deliver(message)) Nodes[(int)message[1]].Receive(message);
                }
                if (!work) return;
            }
            throw new Exception("Unbounded synchronous message loop");
        }
        public void Dispose() { foreach (Node n in Nodes) n.Dispose(); }
    }
    private static int Phase(uint[] m) { return (int)((m[8] >> 8) & 255u); }
    public static int Main()
    {
        try
        {
            Reservoir(); Geometry(); Observation(); Local(); ConfirmedAndDuplicate();
            AbortAndLateAuthorization(); UnknownOutcome(); ReplacementAndPooling();
            SimultaneousFinalHit(); SpawnOrdering(); InvalidAndExpired(); BoundedLoad();
            System.Console.WriteLine("Accretion production protocol/state checks passed: " + checks);
            return 0;
        }
        catch (Exception ex) { System.Console.Error.WriteLine(ex); return 1; }
    }
    private static void Reservoir()
    {
        var p = new OrreryAccretionReservoir(); float spent; ulong a, b;
        p.Begin(1, 12, 10);
        Check(p.TryAbsorb(100, 0, out spent), "Full final hit must veto application");
        Near(spent, 12, "Final cost"); Near(spent * .3f, 3.6f, "Final healing");
        Check(!p.TryAbsorb(100, 0, out spent), "Second independent hit must pass");
        p.Begin(2, 100, 10);
        Check(p.TryAbsorb(40, 0, out spent), "Ordinary absorption"); Near(p.Remaining, 60, "Ordinary remaining");
        Check(p.TryReserve(2, 100, 0, out a), "Reserve final projectile");
        Near(p.Remaining, 60, "Reservation is not expenditure"); Near(p.Available, 0, "One grace event only");
        Check(!p.TryReserve(2, 1, 0, out b), "No simultaneous final-grace ticket");
        Check(!p.TryAbsorb(1, 0, out spent), "Direct hit cannot spend reserved capacity");
        Check(p.Settle(2, a, true, out spent), "Confirmed capture"); Near(spent, 60, "Confirmed cost");
        Check(!p.Settle(2, a, true, out spent), "Duplicate confirmation");
        p.Begin(3, 100, 10); Check(p.TryReserve(3, 60, 0, out a), "Reserve then abort");
        Check(p.Settle(3, a, false, out spent), "Explicit abort accepted"); Near(p.Available, 100, "Abort refunds earmark");
        Near(spent, 0, "Abort cannot heal");
        Check(p.TryReserve(3, 60, 0, out a), "Old generation reserve");
        p.Begin(4, 100, 10); Check(!p.Settle(3, a, true, out spent), "Replacement rejects late result");
        Check(!p.TryAbsorb(1, 10, out spent), "Expiry checked before hit");
        Check(!p.TryAbsorb(0, 0, out spent) && !p.TryAbsorb(-1, 0, out spent) &&
            !p.TryAbsorb(float.NaN, 0, out spent) && !p.TryAbsorb(float.PositiveInfinity, 0, out spent), "Invalid values");
        p.Begin(5, 1, 10); Check(p.TryAbsorb(1, 0, out spent), "Exact exhaustion");
        Check(!p.CanAbsorb(0), "Reentrant callback sees collapse before healing");
        p.Begin(6, 100, 10); var ids = new HashSet<ulong>();
        for (int i = 0; i < OrreryAccretionReservoir.TicketLimit; i++)
        { Check(p.TryReserve(6, .5f, 0, out a), "Bounded ticket allocation"); Check(ids.Add(a), "Unique ticket"); }
        Check(!p.TryReserve(6, .5f, 0, out a), "Ticket bound");
        foreach (ulong id in ids) Check(p.Settle(6, id, true, out spent), "Ticket settlement");
        Near(p.Remaining, 68, "Many tickets conserve capacity");
        p.Begin(7, float.MaxValue, 10); Check(p.TryAbsorb(float.MaxValue, 0, out spent), "Large finite final hit");
        Near(p.Remaining, 0, "No overflow");
    }
    private static void Geometry()
    {
        float at;
        Check(CoreCaptureGeometry.CircleEntry(0,0,1,0,20,10,0,5,out at), "Fast crossing"); Near(at,5,"Entry distance");
        Check(!CoreCaptureGeometry.CircleEntry(0,0,1,0,4,10,0,5,out at), "Earlier obstacle wins");
        Check(CoreCaptureGeometry.CircleEntry(10,0,0,0,0,10,0,5,out at), "Stationary mine inside"); Near(at,0,"Inside entry");
        Check(!CoreCaptureGeometry.CircleEntry(0,6,1,0,20,10,0,5,out at), "Miss");
        Check(CoreCaptureGeometry.CircleEntry(0,5,1,0,20,10,0,5,out at), "Tangent"); Near(at,10,"Tangent position");
        Check(CoreCaptureGeometry.CircleEntry(0,6,1,0,20,10,0,6,out at), "Projectile radius expansion");
        Check(!CoreCaptureGeometry.CircleEntry(float.NaN,0,1,0,20,10,0,5,out at), "Invalid sweep rejected");
    }
    private static void Observation()
    {
        object segment = new object(), head = new object();
        int w = CoreDamageApplicationObservation.BeginWatch(segment);
        int outer = CoreDamageApplicationObservation.BeginApplication(segment);
        int inner = CoreDamageApplicationObservation.BeginApplication(head);
        CoreDamageApplicationObservation.NativeBoundary(true);
        CoreDamageApplicationObservation.EndApplication(inner);
        CoreDamageApplicationObservation.EndApplication(outer);
        Check(CoreDamageApplicationObservation.WatchedApplicationBlocked(), "Forwarded segment inherits single head veto");
        CoreDamageApplicationObservation.EndWatch(w);
        w = CoreDamageApplicationObservation.BeginWatch(head);
        outer = CoreDamageApplicationObservation.BeginApplication(head);
        CoreDamageApplicationObservation.NativeBoundary(false);
        inner = CoreDamageApplicationObservation.BeginApplication(segment);
        CoreDamageApplicationObservation.NativeBoundary(true);
        CoreDamageApplicationObservation.EndApplication(inner);
        CoreDamageApplicationObservation.EndApplication(outer);
        Check(!CoreDamageApplicationObservation.WatchedApplicationBlocked(), "Reflected child does not relabel parent");
        CoreDamageApplicationObservation.EndWatch(w);
        w = CoreDamageApplicationObservation.BeginWatch(head);
        outer = CoreDamageApplicationObservation.BeginApplication(head);
        CoreDamageApplicationObservation.NativeBoundary(true);
        CoreDamageApplicationObservation.EndApplication(outer);
        outer = CoreDamageApplicationObservation.BeginApplication(head);
        CoreDamageApplicationObservation.NativeBoundary(false);
        CoreDamageApplicationObservation.EndApplication(outer);
        Check(CoreDamageApplicationObservation.WatchedApplicationBlocked(), "Later conduit/tick cannot replace first observed hit");
        CoreDamageApplicationObservation.EndWatch(w);
        Check(!CoreDamageApplicationObservation.WatchedApplicationBlocked(), "No sticky blocked-hit flag");
    }
    private static void Local()
    {
        using (var p = new Peers())
        {
            Node n = p.Nodes[0]; n.Field(1,12); uint id = n.Spawn(100);
            Check(n.Sweep(id), "Local authoritative sweep captures"); Check(n.Captured(id), "Native capture happened");
            Near(n.Value("heal"),3.6f,"Local capture healing"); Near(n.Value("remaining"),0,"Local collapse");
            Check(!(bool)n.Call("Damage",100f), "Hit after local collapse passes");
            Check(!(bool)n.Call("OrdinaryExplosion",id), "Capture bypasses ordinary explosion");
        }
    }
    private static void ConfirmedAndDuplicate()
    {
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,100);
            p.Advance(.01f); p.Pump(); uint id=owner.Spawn(40);
            Check(owner.Sweep(id), "Remote field contact"); Check(owner.Held(id),"Projectile held until authorized");
            Near(target.Value("heal"),0,"Dispatch cannot heal"); Check(!owner.Captured(id),"Dispatch cannot capture");
            uint[] prepare=owner.Drain().Single(m=>Phase(m)==2); target.Receive(prepare); target.Receive(prepare);
            Near(target.Value("remaining"),100,"Prepare not spent"); Near(target.Value("available"),60,"Prepare reserves once");
            uint[][] authorizations=target.Drain();
            foreach (uint[] m in authorizations.Where(m=>Phase(m)==3)) owner.Receive(m);
            Check(owner.Captured(id),"Authority commits capture"); Near(owner.Value("captures"),1,"Duplicate auth cannot recapture");
            Near(target.Value("heal"),0,"No heal before confirmed result");
            uint[] captured=owner.Drain().First(m=>Phase(m)==5);
            target.Receive(captured); target.Receive(captured);
            Near(target.Value("remaining"),60,"Exactly-once confirmed cost"); Near(target.Value("heal"),12,"Exactly-once healing");
            p.Pump(); Near(owner.Value("ownerPending"),0,"Receipt retires sender tombstone");
            target.Receive(prepare); p.Pump(); Near(target.Value("remaining"),60,"Delayed duplicate prepare cannot reserve again");
        }
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[2]; target.Field(1,12); p.Advance(.01f); p.Pump();
            uint id=owner.Spawn(100); owner.Sweep(id);
            p.Pump(m=>Phase(m)!=5); Check(owner.Captured(id),"Capture before lost confirmation"); Near(target.Value("heal"),0,"Lost result not guessed");
            p.Advance(.4f); p.Pump(); Near(target.Value("heal"),3.6f,"Retry confirms final hit once");
            Near(target.Value("remaining"),0,"Retry settles final capacity"); Near(owner.Value("captures"),1,"Retry never recaptures");
        }
    }
    private static void AbortAndLateAuthorization()
    {
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,12); p.Advance(.01f); p.Pump();
            uint id=owner.Spawn(100); owner.Sweep(id);
            uint[] prepare=owner.Drain().Single(m=>Phase(m)==2); target.Receive(prepare);
            uint[] auth=target.Drain().First(m=>Phase(m)==3);
            p.Advance(1.5f); p.Pump(m=>Phase(m)!=3);
            Check(!owner.Captured(id)&&!owner.Held(id),"Expired hold resumes original projectile");
            Check((bool)owner.Call("Simulated",id),"Abort restores native physics");
            Near(target.Value("available"),12,"Explicit abort releases reservation"); Near(target.Value("heal"),0,"Abort no healing");
            owner.Receive(auth); p.Pump(); Check(!owner.Captured(id),"Late authorization cannot capture after abort");
        }
    }
    private static void UnknownOutcome()
    {
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,100); p.Advance(.01f); p.Pump();
            uint id=owner.Spawn(40); owner.Sweep(id); uint[] late=null;
            p.Pump(m=> { if(Phase(m)==5){late=m;return false;} return true; });
            Check(owner.Captured(id),"Unknown case really destroyed projectile");
            p.Advance(9f); p.Pump(m=>Phase(m)!=5);
            Near(target.Value("faults"),1,"Uncertain timeout retires field"); Near(target.Value("available"),0,"Unknown must NEVER refund");
            Near(target.Value("heal"),0,"Unknown must NEVER heal");
            target.Field(2,200); target.Receive(late); p.Pump();
            Near(target.Value("remaining"),200,"Late result cannot mutate replacement"); Near(target.Value("heal"),0,"Late old result cannot heal new ship");
        }
    }
    private static void ReplacementAndPooling()
    {
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,100); p.Advance(.01f); p.Pump();
            uint id=owner.Spawn(40); owner.Sweep(id); target.Receive(owner.Drain().Single(m=>Phase(m)==2));
            uint[] auth=target.Drain().First(m=>Phase(m)==3);
            target.Field(2,200); owner.Receive(auth); p.Pump();
            Near(target.Value("remaining"),200,"Recast preserves replacement capacity"); Near(target.Value("heal"),0,"Recast no stale healing");
        }
        using (var p = new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,100); p.Advance(.01f); p.Pump();
            uint id=owner.Spawn(40); owner.Sweep(id); target.Receive(owner.Drain().Single(m=>Phase(m)==2));
            uint[] auth=target.Drain().First(m=>Phase(m)==3);
            owner.Call("Reuse",id); owner.Receive(auth); p.Pump();
            Check(!owner.Captured(id),"Pool reuse cannot destroy replacement object"); Near(target.Value("available"),100,"Pool abort settles without spend");
        }
    }
    private static void SimultaneousFinalHit()
    {
        using (var p = new Peers())
        {
            Node a=p.Nodes[0], b=p.Nodes[2], t=p.Nodes[1]; t.Field(1,12); p.Advance(.01f); p.Pump();
            uint x=a.Spawn(100), y=b.Spawn(100); Check(a.Sweep(x)&&b.Sweep(y),"Two independent simulators propose");
            p.Pump();
            Check(a.Captured(x)!=b.Captured(y),"Only one projectile receives final grace");
            Near(t.Value("heal"),3.6f,"One final-grace heal"); Near(t.Value("remaining"),0,"One shared reservoir");
            Check(!(bool)t.Call("Damage",100f),"Next independent beam tick passes");
        }
    }
    private static void SpawnOrdering()
    {
        using (var p = new Peers())
        {
            Node n=p.Nodes[0]; n.Field(1,12); uint id=n.Spawn(100);
            n.Call("BeginSpawn",id); Check(n.Sweep(id),"Init collision stops at field");
            Check(n.Held(id)&&!n.Captured(id),"Cannot despawn before launcher registration");
            Near(n.Value("remaining"),12,"Spawn deferral is not absorption");
            n.Call("EndSpawn"); n.Call("FinishSpawn",id);
            Check(n.Captured(id),"Registered projectile captured after launcher completes");
            Near(n.Value("heal"),3.6f,"Deferred capture heals once");
        }
        using (var p = new Peers())
        {
            Node n=p.Nodes[0]; n.Field(1,12); uint id=n.Spawn(100);
            n.Call("BeginSpawn",id); n.Sweep(id); n.Call("EndSpawn");
            n.Call("Damage",100f); n.Call("FinishSpawn",id);
            Check(!n.Captured(id)&&!n.Held(id),"Exhausted field cannot authorize deferred projectile");
            Check((bool)n.Call("Simulated",id),"Deferred rejection restores native physics");
        }
    }
    private static void InvalidAndExpired()
    {
        using (var p = new Peers())
        {
            Node n=p.Nodes[0], t=p.Nodes[1]; t.Field(1,100,.2f); p.Advance(.01f); p.Pump();
            uint id=n.Spawn(0); Check(!n.Sweep(id),"Zero-valued projectile cannot be captured for free");
            id=n.Spawn(float.NaN); Check(!n.Sweep(id),"NaN cost rejected");
            id=n.Spawn(float.PositiveInfinity); Check(!n.Sweep(id),"Infinite cost rejected");
            p.Advance(.3f); p.Pump(); id=n.Spawn(40); Check(!n.Sweep(id),"Expired field cannot capture");
            uint[] bad={0,1,CoreProjectileCapture.TransportEffectId,1,1,0,0,0,(1u<<16)|(99u<<8)|1};
            Check(!(bool)t.Call("Receive",(object)bad),"Unknown protocol phase rejected");
            bad[8]=(1u<<16)|(3u<<8)|1; bad[6]=1;
            Check(!(bool)t.Call("Receive",(object)bad),"Unexpected control payload rejected");
        }
        using(var p=new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,100); p.Advance(.01f); p.Pump();
            owner.Call("ReplaceShip",1); owner.Tick(.02f);
            uint id=owner.Spawn(40); Check(!owner.Sweep(id),"Same-generation field cannot follow replacement ship");
        }
    }
    private static void BoundedLoad()
    {
        using(var p=new Peers())
        {
            Node owner=p.Nodes[0], target=p.Nodes[1]; target.Field(1,10000); p.Advance(.01f); p.Pump();
            var ids=new List<uint>();
            for(int i=0;i<80;i++) { uint id=owner.Spawn(1); ids.Add(id); owner.Sweep(id); }
            Near(owner.Value("ownerPending"),CoreProjectileCapture.StartsPerSecond,"Per-second start bound");
            Check(owner.Drain().Length<=CoreProjectileCapture.SendsPerTick,"Hard per-tick send bound");
            for(int i=1;i<=90;i++) { p.Advance(.01f+i*.08f); p.Pump(); }
            Check(ids.All(id=>!owner.Held(id)),"Every held projectile settles or resumes despite budget pressure");
            Near(owner.Value("ownerPending"),0,"Bounded retries eventually retire all exchanges");
            Near(target.Value("recipientPending"),0,"No leaked reservations under pressure");
            Near(target.Value("faults"),0,"Budget pressure resolves before uncertainty timeout");
            Near(target.Value("heal"),owner.Value("captures")*.3f,"Load accounting matches actual native captures");
        }
    }
}

// Every isolated endpoint executes these exact production services with different
// static fields, just as separate game processes do. Native Unity/socket behavior
// is explicitly NOT certified by this executable.
public static class AccretionProtocolEndpoint
{
    private static readonly OrreryAccretionReservoir pool=new OrreryAccretionReservoir();
    private static readonly Dictionary<uint,Projectile> projectiles=new Dictionary<uint,Projectile>();
    private static uint nextProjectile=1, generation;
    private static float heal,faults,captures;
    private static int spawnToken;
    public static void Init(int id)
    {
        NetSession.instance=new NetSession { localPlayerId=id };
        for(int i=0;i<3;i++)
        {
            NetSession.instance.players.Add(new NetPlayer{playerId=i});
            CoreProjectileAuthority.Ships[i]=new GameShip{Id=i,transform=new Transform{position=new Vector3(10,0,0)}};
        }
        CoreProjectileCapture.Register(1,Read,Reserve,Settle,Fault,(ship,projectile)=>true);
    }
    private static bool Read(out CoreProjectileCapture.Field field)
    {
        field=new CoreProjectileCapture.Field { Ship=CoreProjectileAuthority.Ships[NetSession.instance.localPlayerId],
            Generation=generation, Active=pool.Valid&&pool.Remaining>0&&Time.time<pool.ExpiresAt,
            RadiusWorld=5, RemainingSeconds=Math.Max(0,pool.ExpiresAt-Time.time) };
        return generation!=0;
    }
    private static bool Reserve(uint gen,float value,out ulong ticket)
    { return pool.TryReserve(gen,value,Time.time,out ticket); }
    private static void Settle(uint gen,ulong ticket,bool captured)
    { float spent; if(pool.Settle(gen,ticket,captured,out spent)&&captured) heal+=spent*.3f; }
    private static void Fault(uint gen)
    { if(pool.Generation==gen){faults++;pool.Invalidate();CoreProjectileCapture.Retire(1,gen);} }
    public static void Field(uint gen,float capacity,float seconds)
    { if(generation!=0)CoreProjectileCapture.Retire(1,generation);generation=gen;pool.Begin(gen,capacity,Time.time+seconds);CoreProjectileCapture.FieldChanged(); }
    public static bool Damage(float value)
    { float spent; bool block=pool.TryAbsorb(value,Time.time,out spent); if(block)heal+=spent*.3f;return block; }
    public static uint Spawn(float value,float x)
    {
        uint id=(((uint)NetSession.instance.localPlayerId+1)<<24)|nextProjectile++;
        var p=new Projectile { netId=id, Launcher=new Launcher{Damage=value},
            transform=new Transform{position=new Vector3(x,0,0)},
            rigidBody=new Rigidbody2D{position=new Vector2(x,0),velocity=new Vector2(20,0)},
            OnCapture=()=>captures++ };
        projectiles.Add(id,p);return id;
    }
    public static bool Sweep(uint id,float distance)
    { float at;var p=projectiles[id];return CoreProjectileCapture.TrySweep(p,p.rigidBody.position,new Vector2(1,0),distance,0,out at); }
    public static bool Captured(uint id){return projectiles[id].Destroying;}
    public static bool Held(uint id){return CoreProjectileCapture.IsHeld(projectiles[id]);}
    public static bool OrdinaryExplosion(uint id){return projectiles[id].Explosion;}
    public static bool Simulated(uint id){return projectiles[id].rigidBody.simulated;}
    public static void Reuse(uint id){var p=projectiles[id];CoreProjectileCapture.ForgetProjectile(p);p.netId+=10000;}
    public static void BeginSpawn(uint id){spawnToken=CoreProjectileSpawnGuard.Begin(projectiles[id]);}
    public static void EndSpawn(){CoreProjectileSpawnGuard.End(spawnToken);}
    public static void FinishSpawn(uint id){CoreProjectileSpawnGuard.Flush(projectiles[id].Launcher);}
    public static void ReplaceShip(int peer){CoreProjectileAuthority.Ships[peer]=new GameShip{Id=peer,transform=new Transform{position=new Vector3(10,0,0)}};}
    public static float Value(string name)
    {
        switch(name){case "heal":return heal;case "remaining":return pool.Remaining;case "available":return pool.Available;
            case "faults":return faults;case "captures":return captures;case "ownerPending":return CoreProjectileCapture.PendingOwnerCount;
            case "recipientPending":return CoreProjectileCapture.PendingRecipientCount;default:throw new ArgumentException(name);}
    }
    public static void Tick(float now){Time.time=Time.unscaledTime=now;CoreProjectileCapture.Tick();}
    public static uint[][] Drain(){var result=CoreCrossOwnerEffects.Messages.ToArray();CoreCrossOwnerEffects.Messages.Clear();return result;}
    public static bool Receive(uint[] m)
    { return CoreCrossOwnerEffects.Handler(CoreProjectileAuthority.Ships[NetSession.instance.localPlayerId],(int)m[0],
        new CoreCrossOwnerEffects.GrantPayload(m[3],m[4],m[5],m[6],m[7],m[8])); }
}

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class,AllowMultiple=true)] public sealed class HarmonyPatch:Attribute
    { public HarmonyPatch(Type type,string name){} }
    public static class AccessTools {public static FieldInfo Field(Type t,string name)
        {return t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static);} }
}
namespace UnityEngine
{
    public struct Vector2
    {
        public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}
        public float sqrMagnitude=>x*x+y*y;public Vector2 normalized=>sqrMagnitude>0?this*(1f/(float)Math.Sqrt(sqrMagnitude)):zero;
        public static Vector2 zero=>new Vector2();public static Vector2 operator+(Vector2 a,Vector2 b)=>new Vector2(a.x+b.x,a.y+b.y);
        public static Vector2 operator*(Vector2 a,float b)=>new Vector2(a.x*b,a.y*b);
    }
    public struct Vector3 { public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} }
    public sealed class Transform{public Vector3 position;}
    public sealed class Rigidbody2D{public Vector2 position,velocity;public float angularVelocity;public bool simulated=true;}
    public static class Time{public static float time,unscaledTime;}
    public static class Mathf{public static float Max(float a,float b)=>Math.Max(a,b);public static float Min(float a,float b)=>Math.Min(a,b);
        public static float Clamp(float a,float lo,float hi)=>Math.Min(Math.Max(a,lo),hi);}
    public static class Debug{public static void LogError(object text){System.Console.WriteLine(text);} }
}
namespace StarVortex
{
    public sealed class GameShip{public int Id;public Transform transform;public float HealthMax=100;public bool IsPlayer()=>Id==NetSession.instance.localPlayerId;}
    public class Launcher{public float Damage;}
    public class GravityCannon:Launcher{public float currentShotBonusDamage;}
    public class Projectile
    {
        public bool netRendered,Destroying,Explosion,enabled=true;public uint netId;public Rigidbody2D rigidBody;
        public Transform transform;public Launcher Launcher;public Action OnCapture;
        public bool IsDestroying()=>Destroying;public Launcher GetParentLauncher()=>Launcher;
        public void CaptureDestroy(){CoreProjectileCapture.ForgetProjectile(this);Destroying=true;OnCapture?.Invoke();}
    }
    public sealed class CapturedProjectile:Projectile
    { public float capturedDamagePercentage,CapturedFlatDamage;public bool IsOrbiting()=>false;public bool IsDrawn()=>false; }
    public sealed class NetPlayer{public int playerId;}
    public sealed class NetSession{public static NetSession instance;public static bool InSession=>instance!=null;
        public int localPlayerId;public readonly List<NetPlayer> players=new List<NetPlayer>();}
    public static class NetIds{public static bool IsProjectileNetId(uint id)=>(id>>24)>0;public static int ProjectileOwnerOf(uint id)=>(int)(id>>24)-1;}
}
public static class CoreProjectileAuthority
{
    public static readonly Dictionary<int,GameShip> Ships=new Dictionary<int,GameShip>();
    public static bool TryGetPlayerShip(int id,out GameShip ship)=>Ships.TryGetValue(id,out ship);
}
public static class CoreCrossOwnerEffects
{
    public struct GrantPayload{public uint A,B,C,D,E,F;public GrantPayload(uint a,uint b,uint c,uint d,uint e,uint f){A=a;B=b;C=c;D=d;E=e;F=f;}}
    public delegate bool GrantHandler(GameShip target,int source,GrantPayload payload);
    public static GrantHandler Handler;public static readonly List<uint[]> Messages=new List<uint[]>();
    public static void RegisterHandler(ushort effect,GrantHandler handler){Handler=handler;}
    public static bool RequestGrant(int peer,ushort effect,GrantPayload p)
    {Messages.Add(new uint[]{(uint)NetSession.instance.localPlayerId,(uint)peer,effect,p.A,p.B,p.C,p.D,p.E,p.F});return true;}
}
#endif
