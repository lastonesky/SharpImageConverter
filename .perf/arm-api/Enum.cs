using System;
using System.Linq;
using System.Reflection;

var asm = typeof(System.Runtime.Intrinsics.Arm.AdvSimd).Assembly;
var types = asm.GetTypes()
    .Where(t => t.Namespace == "System.Runtime.Intrinsics.Arm")
    .OrderBy(t => t.FullName);

foreach (var t in types)
{
    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                       .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
        Console.WriteLine($"{t.FullName}|{m.Name}({ps})");
    }
}
