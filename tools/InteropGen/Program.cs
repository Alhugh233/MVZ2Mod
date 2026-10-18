using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Mono.Cecil;
using Mono.Cecil.Rocks;

// 用 Il2CppDumper 输出的 DummyDll 生成 BepInEx interop 代理程序集。
// 与 BepInEx 6.0.0-pre.2 内置 Il2CppInteropManager.RunIl2CppInteropGenerator 的行为对齐：
//   - Generator 1.4.5（与 BepInEx/core/Il2CppInterop.Runtime.dll 1.4.5 匹配）
//   - GameAssemblyPath = null（x86 下 ScanMethodRefs 默认关闭，与 BepInEx 一致）
//
// ⚠️ 已知**不等价**（2026-10-06 实测）：本工具没做 Il2CppInterop 的 unstrip 步骤。
//    BepInEx 在游戏里生成 interop 时会打印
//      [Il2CppInteropGen] Unstripping methods... Restored 14078 methods
//    把被 IL2CPP 裁剪掉的 Unity 重载补回来；本工具不会，产物缺这些方法
//    （典型：UnityEngine.Component.GetComponentInChildren<T>(bool)）。
//    拿本工具的产物当编译引用，会让依赖这些重载的 mod 编译失败 —— 用 0.7.0 跑也一样，
//    所以那是本工具的差异，不是游戏版本差异。
//    ⇒ 正式 interop 一律用 BepInEx 在目标游戏目录里生成（UpdateInteropAssemblies=true 启动一次）；
//      本工具只当「快速签名查询 / 应急」用。
var gameDir = args.Length > 0 ? args[0] : @"E:\Game\PVZ\MVZ2\MVZ2 0.7.0";

// 调试模式：打印指定 dll 中某类型的所有构造器
if (args.Length >= 3 && args[0] == "check")
{
    var asm = AssemblyDefinition.ReadAssembly(args[1]);
    var type = asm.MainModule.Types.FirstOrDefault(t => t.FullName == args[2])
               ?? asm.MainModule.GetAllTypes().FirstOrDefault(t => t.FullName == args[2]);
    if (type == null) { Console.WriteLine("类型未找到"); return 1; }
    foreach (var m in type.Methods.Where(m => m.IsConstructor))
        Console.WriteLine(m.ToString());
    Console.WriteLine("-- interfaces --");
    foreach (var itf in type.Interfaces)
        Console.WriteLine(itf.InterfaceType.FullName);
    Console.WriteLine("-- methods --");
    foreach (var m in type.Methods.Where(m => !m.IsConstructor && !m.IsGetter && !m.IsSetter))
        Console.WriteLine(m.ToString());
    Console.WriteLine("-- properties --");
    foreach (var p in type.Properties)
        Console.WriteLine(p.PropertyType.FullName + " " + p.Name);
    return 0;
}

var dummyDir = Path.Combine(gameDir, "dump_out", "DummyDll");
var unityLibs = Path.Combine(gameDir, "BepInEx", "unity-libs");
var outputDir = Path.Combine(gameDir, "BepInEx", "interop");

if (!Directory.Exists(dummyDir))
{
    Console.Error.WriteLine($"DummyDll 目录不存在: {dummyDir}");
    return 1;
}

// 清空旧的 interop 输出
Directory.CreateDirectory(outputDir);
Directory.EnumerateFiles(outputDir, "*.dll").ToList().ForEach(File.Delete);

// 共享 resolver：所有程序集都注册进它的缓存，
// 保证类型解析时返回的是同一批 AssemblyDefinition 实例（否则生成器内部按引用查找会 KeyNotFound）。
var resolver = new SharedResolver();
resolver.AddSearchDirectory(dummyDir);
resolver.AddSearchDirectory(unityLibs);

var readerParams = new ReaderParameters { AssemblyResolver = resolver, ReadSymbols = false };
var assemblies = new List<AssemblyDefinition>();
foreach (var dll in Directory.EnumerateFiles(dummyDir, "*.dll").OrderBy(f => f))
{
    try
    {
        var asm = AssemblyDefinition.ReadAssembly(dll, readerParams);
        resolver.Register(asm);
        assemblies.Add(asm);
        Console.WriteLine($"read: {asm.Name.Name}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"SKIP {Path.GetFileName(dll)}: {e.Message}");
    }
}

Console.WriteLine($"共读取 {assemblies.Count} 个程序集");

// Il2CppDumper 不把 __Generated 虚拟程序集落盘，但 DummyDll 中有类型引用指向它，
// 生成器 RewriteTypeRef 解析不到会抛 AssemblyResolutionException。
// 这里扫描所有 __Generated 类型引用并创建同名桩程序集（含全部被引用类型）。
var generatedRefs = new Dictionary<string, (string Ns, List<string> Names)>();
var seenRefs = new HashSet<TypeReference>();
void ScanTypeRef(TypeReference tr)
{
    if (tr == null || !seenRefs.Add(tr)) return;
    switch (tr)
    {
        case GenericInstanceType git:
            ScanTypeRef(git.ElementType);
            foreach (var ga in git.GenericArguments) ScanTypeRef(ga);
            return;
        case ArrayType at:
            ScanTypeRef(at.ElementType);
            return;
        case ByReferenceType brt:
            ScanTypeRef(brt.ElementType);
            return;
        case PointerType pt:
            ScanTypeRef(pt.ElementType);
            return;
        case GenericParameter gp:
            foreach (var c in gp.Constraints) ScanTypeRef(c.ConstraintType);
            return;
        case TypeSpecification ts:
            ScanTypeRef(ts.ElementType);
            return;
    }
    if (tr.Scope is not AssemblyNameReference anr || anr.Name != "__Generated") return;
    var names = new List<string>();
    var t = tr;
    while (t != null) { names.Insert(0, t.Name); t = t.DeclaringType; }
    // 取最外层命名空间
    var outer = tr;
    while (outer.DeclaringType != null) outer = outer.DeclaringType;
    var key = outer.Namespace + "::" + string.Join("/", names);
    generatedRefs.TryAdd(key, (outer.Namespace, names));
}
foreach (var asm in assemblies)
{
    foreach (var td in asm.MainModule.GetAllTypes())
    {
        if (td.BaseType != null) ScanTypeRef(td.BaseType);
        foreach (var itf in td.Interfaces) ScanTypeRef(itf.InterfaceType);
        foreach (var f in td.Fields) ScanTypeRef(f.FieldType);
        foreach (var m in td.Methods)
        {
            ScanTypeRef(m.ReturnType);
            foreach (var p in m.Parameters) ScanTypeRef(p.ParameterType);
        }
        foreach (var p in td.Properties) ScanTypeRef(p.PropertyType);
        foreach (var e in td.Events) ScanTypeRef(e.EventType);
    }
}
if (generatedRefs.Count > 0)
{
    var genAsm = AssemblyDefinition.CreateAssembly(
        new AssemblyNameDefinition("__Generated", new Version(0, 0, 0, 0)),
        "__Generated", ModuleKind.Dll);
    var byPath = new Dictionary<string, TypeDefinition>();
    foreach (var (ns, names) in generatedRefs.Values.OrderBy(v => string.Join("/", v.Names)))
    {
        TypeDefinition parent = null;
        var path = "";
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            path = path.Length == 0 ? name : path + "/" + name;
            if (!byPath.TryGetValue(path, out var td))
            {
                var nsForThis = i == 0 ? ns : "";
                td = new TypeDefinition(nsForThis, name,
                    TypeAttributes.Class | TypeAttributes.Sealed |
                    (parent != null ? TypeAttributes.NestedPublic : TypeAttributes.Public));
                if (parent != null) parent.NestedTypes.Add(td);
                else genAsm.MainModule.Types.Add(td);
                byPath[path] = td;
            }
            parent = byPath[path];
        }
    }
    resolver.Register(genAsm);
    assemblies.Add(genAsm);
    Console.WriteLine($"创建 __Generated 桩程序集，含 {generatedRefs.Count} 个被引用类型");
}

Console.WriteLine("开始生成代理...");

var opts = new GeneratorOptions
{
    GameAssemblyPath = null, // 与 BepInEx 在 32 位进程下的默认行为一致（不做 xref 扫描）
    Source = assemblies,
    OutputDir = outputDir,
    UnityBaseLibsDir = Directory.Exists(unityLibs) ? unityLibs : null,
    ObfuscatedNamesRegex = null,
};

Il2CppInteropGenerator.Create(opts)
    .AddInteropAssemblyGenerator()
    .Run();

assemblies.ForEach(a => a.Dispose());
Console.WriteLine("完成。");
return 0;

/// <summary>公开 Register 的 DefaultAssemblyResolver（与 Il2CppInterop.CecilMetadataAccess 一致）。</summary>
internal sealed class SharedResolver : DefaultAssemblyResolver
{
    public void Register(AssemblyDefinition asm) => RegisterAssembly(asm);
}
