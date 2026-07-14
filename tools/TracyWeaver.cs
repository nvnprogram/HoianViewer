#:package Mono.Cecil@0.11.6
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// TracyWeaver — .NET 10 file-based build tool. Run straight from source (no project):
//
//   dotnet run TracyWeaver.cs -- --dir <outDir> [--frame-mark Type::Method] Asm1.dll Asm2.dll ...
//
// For each eligible method it injects, at entry:  ctx = TracyWeave.Begin("Type.Method");
// and wraps the original body in a try/finally whose finally calls TracyWeave.End(ctx)
// (plus TracyWeave.FrameMark() in the configured frame method). The TracyWeave shim itself is
// compiled into one of the target assemblies (PlayerViewer.dll) and found by reflection here.
//
// Idempotent: a woven module is stamped with an AssemblyMetadata("TracyWoven") marker and
// skipped on re-runs.

const int MinInstructions = 10; // skip trivial/inlinable leaf methods

string dir = null,
    frameMark = null;
var targets = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dir":
            dir = args[++i];
            break;
        case "--frame-mark":
            frameMark = args[++i];
            break;
        default:
            targets.Add(args[i]);
            break;
    }
}
if (dir == null || targets.Count == 0)
{
    Console.Error.WriteLine(
        "usage: TracyWeaver.cs -- --dir <outDir> [--frame-mark Type::Method] Asm.dll ..."
    );
    return 2;
}

// Open every target module up front (the shim host is among them).
var resolver = MakeResolver(dir);
var modules = new Dictionary<string, ModuleDefinition>();
foreach (var name in targets)
{
    string path = Path.Combine(dir, name);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"[tracy-weave] missing {path}, skipping");
        continue;
    }
    modules[name] = ModuleDefinition.ReadModule(
        path,
        new ReaderParameters
        {
            ReadWrite = true,
            ReadSymbols = false,
            AssemblyResolver = resolver,
        }
    );
}

var shim = modules
    .Values.Select(m => m.GetType("TracyWeaver.Runtime.TracyWeave"))
    .FirstOrDefault(t => t != null);
if (shim == null)
{
    Console.Error.WriteLine(
        "[tracy-weave] TracyWeaver.Runtime.TracyWeave not found in any target assembly"
    );
    return 3;
}
var ctxDef = shim.NestedTypes.Single(t => t.Name == "Ctx");
var beginDef = shim.Methods.Single(m => m.Name == "Begin");
var endDef = shim.Methods.Single(m => m.Name == "End");
var frameMarkDef = shim.Methods.Single(m => m.Name == "FrameMark");

int totalWoven = 0;
var toWrite = new List<ModuleDefinition>();
foreach (var (name, module) in modules)
{
    if (IsWoven(module))
    {
        Console.WriteLine($"[tracy-weave] {name}: already woven, skipping");
        continue;
    }

    var ctxType = module.ImportReference(ctxDef); // same-module for the shim host, cross-module otherwise
    var beginRef = module.ImportReference(beginDef);
    var endRef = module.ImportReference(endDef);
    var frameMarkRef = module.ImportReference(frameMarkDef);

    int woven = 0;
    foreach (var type in AllTypes(module.Types))
    {
        if (HasNoProfile(type))
            continue; // excludes the TracyWeave shim itself
        foreach (var method in type.Methods.ToList())
        {
            if (!Eligible(method))
                continue;
            bool markFrame =
                frameMark != null
                && (method.DeclaringType.FullName + "::" + method.Name) == frameMark;
            try
            {
                Weave(method, ctxType, beginRef, endRef, markFrame ? frameMarkRef : null);
                woven++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tracy-weave] skipped {method.FullName}: {ex.Message}");
            }
        }
    }

    StampWoven(module);
    toWrite.Add(module);
    Console.WriteLine($"[tracy-weave] {name}: wove {woven} methods");
    totalWoven += woven;
}

// Write after all imports are done (cross-module refs into the shim host stay valid).
foreach (var module in toWrite)
    module.Write();
foreach (var module in modules.Values)
    module.Dispose();

Console.WriteLine($"[tracy-weave] done ({totalWoven} methods across {modules.Count} assemblies)");
return 0;

// ---- helpers ----

// Resolver that falls back to the NuGet cache for refs absent from the output dir (e.g. the
// Windows-only System.Drawing.Common, which Cecil must still resolve to write the module).
static DefaultAssemblyResolver MakeResolver(string dir)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(dir);
    string nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
    if (string.IsNullOrEmpty(nuget))
        nuget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages"
        );
    resolver.ResolveFailure += (sender, name) =>
    {
        string pkgDir = Path.Combine(nuget, name.Name.ToLowerInvariant());
        if (!Directory.Exists(pkgDir))
            return null;
        var hit = Directory
            .EnumerateFiles(pkgDir, name.Name + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(TfmRank)
            .FirstOrDefault();
        if (hit == null)
            return null;
        try
        {
            return AssemblyDefinition.ReadAssembly(
                hit,
                new ReaderParameters { AssemblyResolver = (IAssemblyResolver)sender }
            );
        }
        catch
        {
            return null;
        }
    };
    return resolver;
}

// Prefer a modern managed TFM's copy of the assembly.
static int TfmRank(string path)
{
    string[] order =
    {
        "net10.0",
        "net9.0",
        "net8.0",
        "net7.0",
        "net6.0",
        "netstandard2.1",
        "netstandard2.0",
    };
    for (int i = 0; i < order.Length; i++)
        if (path.Contains(Path.DirectorySeparatorChar + order[i] + Path.DirectorySeparatorChar))
            return order.Length - i;
    return 0;
}

static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
{
    foreach (var t in types)
    {
        yield return t;
        foreach (var n in AllTypes(t.NestedTypes))
            yield return n;
    }
}

static bool Eligible(MethodDefinition m)
{
    if (!m.HasBody || m.IsAbstract || m.IsPInvokeImpl || m.IsInternalCall || m.IsRuntime)
        return false;
    if (m.IsConstructor)
        return false; // avoid ctor/base-call verification pitfalls
    if (m.IsGetter || m.IsSetter || m.IsAddOn || m.IsRemoveOn)
        return false;
    if (m.Name.StartsWith("op_", StringComparison.Ordinal))
        return false;
    if (m.ReturnType.IsByReference)
        return false; // ref-returns complicate value capture
    if (m.DeclaringType.IsInterface)
        return false;
    if (IsGenerated(m.Name) || IsGenerated(m.DeclaringType.Name))
        return false;
    if (HasNoProfile(m) || HasCompilerGenerated(m) || HasCompilerGenerated(m.DeclaringType))
        return false;
    if (m.Body.Instructions.Count < MinInstructions)
        return false;
    return true;
}

// Lambdas, local functions, iterator/async state machines, etc. carry '<' in their names.
static bool IsGenerated(string name) => name.IndexOf('<') >= 0;

static bool HasNoProfile(ICustomAttributeProvider p) =>
    p.HasCustomAttributes
    && p.CustomAttributes.Any(a => a.AttributeType.Name == "NoProfileAttribute");

static bool HasCompilerGenerated(ICustomAttributeProvider p) =>
    p.HasCustomAttributes
    && p.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute");

static void Weave(
    MethodDefinition m,
    TypeReference ctxType,
    MethodReference beginRef,
    MethodReference endRef,
    MethodReference frameMarkRef
)
{
    var body = m.Body;
    body.InitLocals = true;
    //Expand short-form branches to long form so inserting instructions can't push a target out
    //of a short branch's ±127 range; OptimizeMacros re-shortens what still fits after weaving.
    body.SimplifyMacros();
    var il = body.GetILProcessor();

    var ctxVar = new VariableDefinition(ctxType);
    body.Variables.Add(ctxVar);

    var first = body.Instructions[0];
    string zone = m.DeclaringType.Name + "." + m.Name;

    // Prologue (outside the try): ctx = Begin("Type.Method");
    il.InsertBefore(first, il.Create(OpCodes.Ldstr, zone));
    il.InsertBefore(first, il.Create(OpCodes.Call, beginRef));
    il.InsertBefore(first, il.Create(OpCodes.Stloc, ctxVar));

    // Return-value capture local (for non-void methods).
    VariableDefinition retVar = null;
    if (m.ReturnType.MetadataType != MetadataType.Void)
    {
        retVar = new VariableDefinition(m.ReturnType);
        body.Variables.Add(retVar);
    }

    // Epilogue (after the finally): [ldloc retVar]; ret
    var retInstr = Instruction.Create(OpCodes.Ret);
    var loadRet = retVar != null ? Instruction.Create(OpCodes.Ldloc, retVar) : null;
    var leaveTarget = loadRet ?? retInstr;

    // Finally handler: ldloc ctx; End(ctx); [FrameMark();] endfinally
    var handlerStart = Instruction.Create(OpCodes.Ldloc, ctxVar);
    var endfinally = Instruction.Create(OpCodes.Endfinally);

    // Redirect every existing ret to flow through the finally: capture value (if any), then leave.
    foreach (var ins in body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
    {
        if (retVar != null)
        {
            ins.OpCode = OpCodes.Stloc;
            ins.Operand = retVar; // reuse object so branch targets stay valid
            il.InsertAfter(ins, Instruction.Create(OpCodes.Leave, leaveTarget));
        }
        else
        {
            ins.OpCode = OpCodes.Leave;
            ins.Operand = leaveTarget;
        }
    }

    // Append handler + epilogue at the end of the method.
    il.Append(handlerStart);
    il.Append(Instruction.Create(OpCodes.Call, endRef));
    if (frameMarkRef != null)
        il.Append(Instruction.Create(OpCodes.Call, frameMarkRef));
    il.Append(endfinally);
    if (loadRet != null)
        il.Append(loadRet);
    il.Append(retInstr);

    body.ExceptionHandlers.Add(
        new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = first,
            TryEnd = handlerStart, // exclusive
            HandlerStart = handlerStart,
            HandlerEnd = leaveTarget, // exclusive
        }
    );

    body.OptimizeMacros(); // re-shorten branches/locals that fit
}

static bool IsWoven(ModuleDefinition module) =>
    module.Assembly.CustomAttributes.Any(a =>
        a.AttributeType.Name == "AssemblyMetadataAttribute"
        && a.ConstructorArguments.Count == 2
        && (a.ConstructorArguments[0].Value as string) == "TracyWoven"
    );

static void StampWoven(ModuleDefinition module)
{
    var ctor = module.ImportReference(
        typeof(System.Reflection.AssemblyMetadataAttribute).GetConstructor(
            new[] { typeof(string), typeof(string) }
        )
    );
    var attr = new CustomAttribute(ctor);
    attr.ConstructorArguments.Add(
        new CustomAttributeArgument(module.TypeSystem.String, "TracyWoven")
    );
    attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, "1"));
    module.Assembly.CustomAttributes.Add(attr);
}
