// 反射探测工具：列出程序集中的类型与成员。
//
// 2026-11-23 之前，这个工具是 inpoutx64 的物理内存探测器（MapPhysToLin +
// 0xE0000000 / 0xC0000000 / 0xF0000000 三个基址依次映射），正是当天三次
// 0x1EA THREAD_STUCK_IN_DEVICE_DRIVER 蓝屏的那条探测路径；而且 dll 路径写死成
// 作者另一台机器的绝对路径，换机器即失效（等于一颗定时炸弹）。
//
// 现在它只做**纯反射**：不加载任何驱动、不碰物理内存、不写死任何路径。
using System;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("用法: DumpTypes <程序集.dll> [类型名过滤]");
            return 1;
        }

        string path = Path.GetFullPath(args[0]);
        if (!File.Exists(path)) { Console.WriteLine("找不到: " + path); return 2; }

        Assembly asm;
        try { asm = Assembly.LoadFrom(path); }
        catch (Exception ex) { Console.WriteLine("加载失败: " + ex.Message); return 3; }

        string filter = args.Length > 1 ? args[1] : "";
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        Console.WriteLine("程序集: " + asm.FullName);
        Console.WriteLine("类型数: " + types.Length);
        foreach (Type t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (filter.Length > 0 && !(t.FullName ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
            Console.WriteLine();
            Console.WriteLine((t.IsPublic ? "public " : "internal ") + kind + " " + t.FullName);
            foreach (MemberInfo m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                               | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine("    " + m.MemberType.ToString().PadRight(8) + " " + m);
            }
        }
        return 0;
    }
}
