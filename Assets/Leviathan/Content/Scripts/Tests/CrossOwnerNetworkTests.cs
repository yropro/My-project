#if CROSS_OWNER_NETWORK_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;

// Compile with the unmodified CoreCrossOwnerEffects.cs only. Native transport,
// JSON and Unity are test doubles; grant routing/validation is production code.
namespace HarmonyLib
{
    public class HarmonyPatch : Attribute { public HarmonyPatch() {} public HarmonyPatch(Type t, string name) {} }
    public static class AccessTools
    {
        public static FieldInfo Field(Type t, string name) { return t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        public static MethodInfo Method(Type t, string name) { return t.GetMethod(name); }
    }
}
namespace UnityEngine
{
    public static class Time { public static float unscaledTime; }
    public static class Debug
    {
        public static void LogError(object value) { System.Console.WriteLine(value); }
        public static void LogWarning(object value) { System.Console.WriteLine(value); }
    }
    public static class JsonUtility
    {
        public static string ToJson(object value)
        {
            var parts = new List<string>();
            foreach (var field in value.GetType().GetFields()) parts.Add(field.Name + "=" + field.GetValue(value));
            return string.Join(";", parts.ToArray());
        }
        public static T FromJson<T>(string text)
        {
            object value = Activator.CreateInstance(typeof(T), true);
            foreach (string item in text.Split(';'))
            {
                string[] pair = item.Split('=');
                typeof(T).GetField(pair[0]).SetValue(value, int.Parse(pair[1]));
            }
            return (T)value;
        }
    }
}
namespace StarVortex
{
    public enum NetMessageType : byte { Native = 1 }
    public sealed class GameShip { public bool IsPlayer() { return true; } }
    public sealed class NetPlayer { public int currentStarId = 7; }
    public sealed class WorldController
    {
        public static WorldController instance = new WorldController();
        public GameShip Ship = new GameShip();
        public GameShip GetCurrentPlayerShip() { return Ship; }
    }
    public sealed class NetSession
    {
        public static NetSession instance;
        public static bool InSession = true, IsHost;
        public int localPlayerId;
        public Dictionary<object, int> connToPlayer = new Dictionary<object, int>();
        public Dictionary<int, NetPlayer> Players = new Dictionary<int, NetPlayer>();
        public List<string> Sent = new List<string>();
        public NetPlayer GetPlayer(int id) { NetPlayer p; return Players.TryGetValue(id, out p) ? p : null; }
        public void SendStarEntityMessage(NetMessageType type, string json, int star) { Sent.Add(json); }
    }
}
public static class CrossOwnerNetworkTests
{
    private static int checks, applied, noticed;
    private static bool accept = true;
    private static CoreCrossOwnerEffects.GrantNotice last;
    private static void Assert(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    public static int Main()
    {
        try { Run(); System.Console.WriteLine("Cross-owner routing checks passed: " + checks); return 0; }
        catch (Exception ex) { System.Console.Error.WriteLine(ex); return 1; }
    }
    private static string Envelope(int source, int target, int sequence)
    {
        return "version=1;sourcePlayerId=" + source + ";targetPlayerId=" + target +
            ";starId=7;sequence=" + sequence + ";effectId=513;payloadA=1;payloadB=0;payloadC=0;payloadD=0;payloadE=0;payloadF=0";
    }
    private static void Run()
    {
        var session = new StarVortex.NetSession { localPlayerId = 1 };
        StarVortex.NetSession.instance = session;
        for (int i = 1; i <= 3; i++) session.Players.Add(i, new StarVortex.NetPlayer());
        session.connToPlayer.Add("peer2", 2);
        CoreCrossOwnerEffects.RegisterHandler(513, (ship, source, payload) => { applied++; return accept; });
        CoreCrossOwnerEffects.RegisterObserver(513, (s, notice) => { noticed++; last = notice; });
        StarVortex.NetSession.IsHost = true;
        CoreCrossOwnerEffects.ReceiveAtHost(session, "peer2", Envelope(999, 1, 1));
        Assert(applied == 1 && noticed == 1 && last.SourcePlayerId == 2, "Host must canonicalize sender before observation");
        Assert(session.Sent.Count == 1 && session.Sent[0].Contains("sourcePlayerId=2"), "Host-target grant must reach observers");
        CoreCrossOwnerEffects.ReceiveAtHost(session, "peer2", Envelope(2, 1, 1));
        Assert(applied == 1, "Duplicate grant reapplied gameplay");
        int previous = noticed;
        session.Players[2].currentStarId = 8;
        CoreCrossOwnerEffects.ReceiveAtHost(session, "peer2", Envelope(2, 1, 2));
        Assert(noticed == previous && applied == 1, "Invalid star grant leaked to observer");
        session.Players[2].currentStarId = 7;
        accept = false;
        CoreCrossOwnerEffects.ReceiveAtHost(session, "peer2", Envelope(2, 1, 3));
        Assert(noticed == previous, "Rejected host grant produced visual");
        accept = true;
        StarVortex.NetSession.IsHost = false;
        session.localPlayerId = 3;
        int before = applied;
        CoreCrossOwnerEffects.ReceiveAtClient(session, Envelope(2, 1, 4));
        Assert(noticed == previous + 1 && applied == before, "Observer peer must never author gameplay");
        session.localPlayerId = 1;
        CoreCrossOwnerEffects.ReceiveAtClient(session, Envelope(2, 1, 5));
        Assert(applied == before + 1 && last.TargetPlayerId == 1, "Recipient must apply validated grant");
        accept = false; previous = noticed;
        CoreCrossOwnerEffects.ReceiveAtClient(session, Envelope(2, 1, 6));
        Assert(noticed == previous, "Rejected recipient grant produced visual");
        CoreCrossOwnerEffects.ReceiveAtClient(session, "malformed");
        Assert(noticed == previous, "Malformed grant produced visual");
        session.localPlayerId = 2;
        previous = noticed;
        Assert(CoreCrossOwnerEffects.RequestGrant(1, 513, default(CoreCrossOwnerEffects.GrantPayload)), "Client send failed");
        Assert(noticed == previous, "Client must await host relay before observing");
        CoreCrossOwnerEffects.ReceiveAtClient(session, session.Sent[session.Sent.Count - 1]);
        Assert(noticed == previous + 1, "Source should observe host relay");
    }
}
#endif
