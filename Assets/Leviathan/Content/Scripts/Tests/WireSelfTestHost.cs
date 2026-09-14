#if CORE_WIRE_TEST_HOST
using System;
namespace UnityEditor
{
    public class MenuItem : Attribute { public MenuItem(string path) {} }
}
namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return default(Vector2); } }
        public static Vector2 one { get { return new Vector2(1, 1); } }
        public static bool operator ==(Vector2 a, Vector2 b) { return a.x == b.x && a.y == b.y; }
        public static bool operator !=(Vector2 a, Vector2 b) { return !(a == b); }
        public override bool Equals(object value) { return value is Vector2 && this == (Vector2)value; }
        public override int GetHashCode() { return x.GetHashCode() ^ y.GetHashCode(); }
    }
    public static class Debug
    {
        public static void Log(object value) { Console.WriteLine(value); }
        public static void LogError(object value) { Console.WriteLine(value); }
    }
}
public static class WireSelfTestHost
{
    public static int Main()
    {
        CoreWireSelfTest.Run();
        return (int)typeof(CoreWireSelfTest).GetField("failures",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null) == 0 ? 0 : 1;
    }
}
#endif
