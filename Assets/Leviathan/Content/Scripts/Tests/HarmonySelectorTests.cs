#if CORE_SELECTOR_TESTS
using System;
using System.IO;
using System.Reflection;
using System.Collections;
class Check {
 static int Main() {
 AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
 foreach(var dir in Environment.GetEnvironmentVariable("CORE_NETWORK_TEST_ASSEMBLIES").Split(';')) {
 var path=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll"); if(File.Exists(path))return Assembly.LoadFrom(path);
 } return null; };
 var a=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"LeviathanMod.dll"));
 var router=a.GetType("OrreryDamageRouter").GetMethod("EnsureAvailable",BindingFlags.Static|BindingFlags.NonPublic);
 if(!(bool)router.Invoke(null,null))throw new Exception("Native damage router did not bind");
 Console.WriteLine("Native integer damage router: bound successfully");
 int passed=0,failed=0;
 foreach(var t in a.GetTypes()) foreach(var m in t.GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) {
 if((m.Name!="TargetMethod" && m.Name!="TargetMethods") || m.GetParameters().Length!=0)continue;
 try { var result=m.Invoke(null,null); if(result==null)throw new Exception("null target");
 if(result is IEnumerable)foreach(var target in (IEnumerable)result)if(target==null)throw new Exception("null target entry");
 passed++; } catch(Exception ex) {failed++; Console.WriteLine(t.Name+": "+ex.GetBaseException().Message);}
 }
 Console.WriteLine("Target selectors passed: "+passed+"; failed: "+failed); return failed==0?0:1;
 }
}
#endif
