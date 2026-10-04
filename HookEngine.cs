using System.Reflection;
using System.Text;
using Lead.Hook.Runtime;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Lead.Hook;

public sealed partial class HookEngine
{
    private readonly List<HookRule> _rules = new();
    //_replacementAssemblies 已登记的替换方程序集 键是程序集简单名
    private readonly Dictionary<string, Assembly> _replacementAssemblies = new();
    //_replacementSources 替换方的按需加载器
    //规则表可以先按名字建完 真正要用到替换方法时再把它拉起来
    //mod 注入 mod 靠这个把"建规则"与"加载替换方"解耦
    private readonly Dictionary<string, Func<Assembly?>> _replacementSources = new();
    private readonly RuntimeHookEngine? _runtimeEngine;
    private int _rewriteCount;
    private bool _sealed;
    //_runtimePatchesApplied RuntimePatch 只执行一次
    //重复执行会让 RuntimeHookEngine 对同一个方法抛重复注册 这里先挡住
    private bool _runtimePatchesApplied;

    //标签按参数分桶时用到的 BCL 方法 只在插桩时解析一次
    private static readonly MethodInfo StringConcat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo ObjectToString = typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;

    public int RewriteCount => _rewriteCount;
    public IReadOnlyList<HookRule> Rules => _rules.AsReadOnly();
    public RuntimeHookEngine? RuntimeEngine => _runtimeEngine;

    public HookEngine()
    {
        _runtimeEngine = new RuntimeHookEngine();
    }

    public HookEngine AddRule(HookRule rule)
    {
        EnsureNotSealed();
        var added = rule ?? throw new ArgumentNullException(nameof(rule));
        _rules.Add(added);
        TrackReplacement(added);
        return this;
    }

    public HookEngine AddRules(IEnumerable<HookRule> rules)
    {
        EnsureNotSealed();
        foreach (var rule in rules)
        {
            _rules.Add(rule);
            TrackReplacement(rule);
        }
        return this;
    }

    //TrackReplacement 规则自带程序集就顺手登记 从名字建的留给调用方注册加载器
    private void TrackReplacement(HookRule rule)
    {
        if (rule.SourceAssembly is null)
            return;
        _replacementAssemblies.TryAdd(rule.ReplacementAssembly, rule.SourceAssembly);
    }

    //RegisterReplacementSource 登记某个程序集的按需加载器
    //规则只用名字记替换方 改写命中时才需要它真的在场 那时走这里把它取来
    //替换方自己也被注入时它的加载会递归走同一套改写 因此不必预先排加载顺序
    public HookEngine RegisterReplacementSource(string assemblyName, Func<Assembly?> loader)
    {
        EnsureNotSealed();
        _replacementSources[assemblyName] = loader ?? throw new ArgumentNullException(nameof(loader));
        return this;
    }

    public HookEngine AddRule(string originalType, string originalMethod, Type replacementType, string replacementMethod, HookType hookType = HookType.CallSite, PatchMode patchMode = PatchMode.ILRewrite, string? description = null)
    {
        return AddRule(new HookRule(originalType, originalMethod, replacementType, replacementMethod, hookType, patchMode, description));
    }

    public HookEngine RemoveRule(string originalType, string originalMethod)
    {
        EnsureNotSealed();
        _rules.RemoveAll(r => r.OriginalType == originalType && r.OriginalMethod == originalMethod);
        return this;
    }

    public HookEngine ClearRules()
    {
        EnsureNotSealed();
        _rules.Clear();
        return this;
    }

    public byte[] Rewrite(string assemblyPath)
    {
        Seal();
        var ilRules = _rules.Where(r => r.PatchMode == PatchMode.ILRewrite).ToList();

        var readerParams = new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            ReadWrite = false,
            InMemory = true
        };

        using var asm = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);
        RewriteAssembly(asm, ilRules, BuildNoOptimizeTargets(_rules));

        using var ms = new MemoryStream();
        asm.Write(ms);
        return ms.ToArray();
    }

    public byte[] Rewrite(byte[] assemblyBytes)
    {
        Seal();
        var ilRules = _rules.Where(r => r.PatchMode == PatchMode.ILRewrite).ToList();

        using var input = new MemoryStream(assemblyBytes);
        var readerParams = new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            ReadWrite = false,
            InMemory = true
        };

        using var asm = AssemblyDefinition.ReadAssembly(input, readerParams);
        RewriteAssembly(asm, ilRules, BuildNoOptimizeTargets(_rules));

        using var ms = new MemoryStream();
        asm.Write(ms);
        return ms.ToArray();
    }

    //_ordinalCounts 当前宿主方法里每条规则已经匹配到第几处
    //ordinal 数的是方法内相对位置 每换一个宿主方法就清一次
    private readonly Dictionary<HookRule, int> _ordinalCounts = new();

    //RewrittenHosts 上一次 RewriteInMemory 里真正被改过的方法体
    //指令级锚点的规则记的是被调用方 推不出宿主有哪些 只能靠改写前后对比找
    internal IReadOnlyList<MethodDefinition> RewrittenHosts { get; private set; } =
        Array.Empty<MethodDefinition>();

    //RewriteInMemory 与 Rewrite 完全相同的改写 只是把结果留在内存对象里交回调用方
    //运行时注入要的是改写后的方法体 序列化那一步它自己做 写盘用不上
    //modes 挑这一趟应用哪些补丁模式的规则 默认只取加载时改写那一类
    //不 dispose 返回值 调用方负责
    internal AssemblyDefinition RewriteInMemory(byte[] assemblyBytes, params PatchMode[] modes)
    {
        Seal();
        if (modes.Length == 0)
            modes = new[] { PatchMode.ILRewrite };
        var ilRules = _rules.Where(r => modes.Contains(r.PatchMode)).ToList();

        using var input = new MemoryStream(assemblyBytes);
        var readerParams = new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            ReadWrite = false,
            InMemory = true
        };

        var asm = AssemblyDefinition.ReadAssembly(input, readerParams);

        var before = SnapshotBodies(asm);
        RewriteAssembly(asm, ilRules, BuildNoOptimizeTargets(_rules));
        RewrittenHosts = CollectChangedBodies(asm, before);
        return asm;
    }

    //SnapshotBodies 把每个方法体的指令序列压成一段文本 用来对比改写前后
    private static Dictionary<string, string> SnapshotBodies(AssemblyDefinition assembly)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                snapshot[MethodKey(method)] = DescribeBody(method.Body);
            }
        }
        return snapshot;
    }

    //CollectChangedBodies 找出与快照不一样的方法 新出现的方法也算
    private static List<MethodDefinition> CollectChangedBodies(
        AssemblyDefinition assembly,
        Dictionary<string, string> snapshot)
    {
        var changed = new List<MethodDefinition>();
        foreach (var type in assembly.MainModule.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;

                if (!snapshot.TryGetValue(MethodKey(method), out var previous) ||
                    previous != DescribeBody(method.Body))
                {
                    changed.Add(method);
                }
            }
        }
        return changed;
    }

    private static string MethodKey(MethodDefinition method) =>
        $"{method.DeclaringType.FullName}::{method.Name}";

    private static string DescribeBody(Mono.Cecil.Cil.MethodBody body)
    {
        var text = new System.Text.StringBuilder();
        foreach (var instruction in body.Instructions)
        {
            text.Append(instruction.OpCode.Value).Append(':').Append(instruction.Operand).Append(';');
        }
        text.Append("|vars:").Append(body.Variables.Count);
        return text.ToString();
    }

    public HookResult RewriteWithResult(string assemblyPath)
    {
        var countBefore = _rewriteCount;
        var bytes = Rewrite(assemblyPath);
        return new HookResult(bytes, _rewriteCount - countBefore, _rules.Count);
    }

    public HookResult RewriteWithResult(byte[] assemblyBytes)
    {
        var countBefore = _rewriteCount;
        var bytes = Rewrite(assemblyBytes);
        return new HookResult(bytes, _rewriteCount - countBefore, _rules.Count);
    }

    public void ApplyRuntimePatch(MethodInfo original, MethodInfo replacement)
    {
        _runtimeEngine?.Patch(original, replacement);
    }

    public void ApplyRuntimePatch(string originalTypeFullName, string methodName, Type replacementType, string replacementMethodName)
    {
        _runtimeEngine?.Patch(originalTypeFullName, methodName, replacementType, replacementMethodName);
    }

    //DumpMethodIl 输出指定方法在本引擎规则下的 IL 便于确认插桩结果
    //改写与反汇编都在内存里完成 不碰磁盘
    public string DumpMethodIl(byte[] assemblyBytes, string typeFullName, string methodName)
    {
        Seal();
        var ilRules = _rules.Where(r => r.PatchMode == PatchMode.ILRewrite).ToList();

        using var input = new MemoryStream(assemblyBytes);
        using var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            ReadWrite = false,
            InMemory = true
        });

        RewriteAssembly(asm, ilRules, BuildNoOptimizeTargets(_rules));

        var type = asm.MainModule.GetType(typeFullName);
        if (type == null)
            return $"未找到类型 {typeFullName}";

        var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
        if (method == null)
            return $"未找到方法 {methodName}";
        if (method.Body == null)
            return $"{methodName} 没有方法体";

        var body = method.Body;
        var builder = new StringBuilder();
        builder.AppendLine($"{typeFullName}::{methodName}  局部变量={body.Variables.Count}  异常处理={body.ExceptionHandlers.Count}");

        foreach (var handler in body.ExceptionHandlers)
            builder.AppendLine($"  EH {handler.HandlerType} try=[{handler.TryStart} .. {handler.TryEnd}) handler=[{handler.HandlerStart} .. {handler.HandlerEnd})");

        foreach (var instruction in body.Instructions)
            builder.AppendLine($"  {instruction}");

        return builder.ToString();
    }

    public bool RemoveRuntimePatch(string originalTypeFullName, string methodName)
    {
        return _runtimeEngine?.Unpatch(originalTypeFullName, methodName) ?? false;
    }

    public void RemoveAllRuntimePatches()
    {
        _runtimeEngine?.UnpatchAll();
    }

    public TDelegate? GetTrampoline<TDelegate>(MethodInfo original) where TDelegate : Delegate
    {
        return _runtimeEngine?.GetTrampoline<TDelegate>(original);
    }

    //ApplyRuntimePatches 执行全部 RuntimePatch 规则 由调用方在目标类型已加载之后调
    //改写时执行太早 那一刻目标程序集正在被加载 目标类型还没进来 FindLoadedType 必然落空
    //内核在预载完目标程序集、模组 Init 之前调这里 时机确定可复现
    public void ApplyRuntimePatches()
    {
        if (_runtimePatchesApplied)
            return;
        _runtimePatchesApplied = true;

        var runtimeRules = _rules.Where(r => r.PatchMode == PatchMode.RuntimePatch).ToList();
        if (runtimeRules.Count == 0)
            return;
        ApplyRuntimePatches(runtimeRules);
    }

    private void ApplyRuntimePatches(List<HookRule> runtimeRules)
    {
        foreach (var rule in runtimeRules)
        {
            var originalType = FindLoadedType(rule.OriginalType);
            if (originalType == null)
            {
                Console.Error.WriteLine($"[Lead.Hook] Runtime patch skipped: type {rule.OriginalType} not found in loaded assemblies");
                continue;
            }

            var originalMethod = originalType.GetMethod(rule.OriginalMethod,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (originalMethod == null)
            {
                Console.Error.WriteLine($"[Lead.Hook] Runtime patch skipped: method {rule.OriginalType}::{rule.OriginalMethod} not found");
                continue;
            }

            var replacement = ResolveReplacement(rule);
            if (replacement is null)
            {
                Console.Error.WriteLine($"[Lead.Hook] Runtime patch skipped: replacement type {rule.ReplacementAssembly}!{rule.ReplacementTypeName} not resolved");
                continue;
            }

            var replacementMethod = replacement.Value.Type.GetMethod(rule.ReplacementMethod,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (replacementMethod == null)
            {
                Console.Error.WriteLine($"[Lead.Hook] Runtime patch skipped: replacement method {rule.ReplacementTypeName}::{rule.ReplacementMethod} not found");
                continue;
            }

            _runtimeEngine?.Patch(originalMethod, replacementMethod);
        }
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(fullName);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }

    private void Seal() => _sealed = true;

    private void EnsureNotSealed()
    {
        if (_sealed)
            throw new InvalidOperationException("Cannot modify rules after the engine has been used for rewriting. Create a new HookEngine instance.");
    }

    private class RuleSet
    {
        public Dictionary<string, List<HookRule>> CallSite = new();
        public Dictionary<string, List<HookRule>> MethodBody = new();
        public Dictionary<string, List<HookRule>> NewObj = new();
        public Dictionary<string, List<HookRule>> FieldRead = new();
        public Dictionary<string, List<HookRule>> FieldWrite = new();
        public Dictionary<string, List<HookRule>> TypeCheck = new();
        public Dictionary<string, List<HookRule>> Box = new();
        public Dictionary<string, List<HookRule>> FunctionPointer = new();
        public Dictionary<string, List<HookRule>> Probe = new();
        public Dictionary<string, List<HookRule>> Mark = new();
        //局部变量与常量这三类的键是**宿主方法** 与其余几类记被引用实体不同
        public Dictionary<string, List<HookRule>> LocalRead = new();
        public Dictionary<string, List<HookRule>> LocalWrite = new();
        public Dictionary<string, List<HookRule>> Constant = new();
    }

    private RuleSet BuildRuleSet(List<HookRule> rules)
    {
        var rs = new RuleSet();
        foreach (var rule in rules)
        {
            var key = $"{rule.OriginalType}::{rule.OriginalMethod}";
            var target = rule.HookType switch
            {
                HookType.CallSite => rs.CallSite,
                HookType.MethodBody => rs.MethodBody,
                HookType.NewObj => rs.NewObj,
                HookType.FieldRead => rs.FieldRead,
                HookType.FieldWrite => rs.FieldWrite,
                HookType.TypeCheck => rs.TypeCheck,
                HookType.Box => rs.Box,
                HookType.FunctionPointer => rs.FunctionPointer,
                HookType.Probe => rs.Probe,
                HookType.Mark => rs.Mark,
                HookType.LocalRead => rs.LocalRead,
                HookType.LocalWrite => rs.LocalWrite,
                HookType.Constant => rs.Constant,
                _ => rs.CallSite
            };
            if (!target.ContainsKey(key))
                target[key] = new List<HookRule>();
            target[key].Add(rule);
        }
        return rs;
    }

    //BuildNoOptimizeTargets 收集需要禁止优化的目标方法 键是 类型全名::方法名
    //只有替换方法体与运行时 patch 这两种模式依赖"目标方法的入口是唯一入口"
    //内联会把目标方法固化进调用方 分层编译还会在方法变热时重编译并把入口指到新代码
    //两者叠加的后果是无论改 IL 还是 patch 入口 都够不着真正在跑的那份实现
    //CallSite 改的是调用点 Probe/Mark 改的是目标自身 二者都在 JIT 之前完成 内联基于改后的 IL 不受影响
    private static HashSet<string> BuildNoOptimizeTargets(List<HookRule> rules)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule.HookType != HookType.MethodBody && rule.PatchMode != PatchMode.RuntimePatch)
                continue;
            targets.Add($"{rule.OriginalType}::{rule.OriginalMethod}");
        }
        return targets;
    }

    //MarkNotOptimized 给目标方法打上不可优化位
    //miNoOptimization 在建表期有两处效果 一是 SetNotInline 禁掉内联与 GDV 候选
    //二是让方法不满足 IsEligibleForTieredCompilation 于是不参与分层 入口不会被 Tier1 重编译重置
    //只标 NoInlining 只能解决前一半 实测中分层提升照样把 patch 的 jmp 冲掉
    private static void MarkNotOptimized(MethodDefinition method)
    {
        if (method.IsAbstract || method.IsPInvokeImpl)
            return;
        method.ImplAttributes |= Mono.Cecil.MethodImplAttributes.NoOptimization;
    }

    private void RewriteAssembly(AssemblyDefinition asm, List<HookRule> ilRules, HashSet<string> noOptimizeTargets)
    {
        var module = asm.MainModule;
        //混入先落 搬进来的方法后面照样会被规则扫到
        ApplyMixins(asm, module);
        var rs = BuildRuleSet(ilRules);

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                var methodKey = $"{type.FullName}::{method.Name}";
                if (noOptimizeTargets.Contains(methodKey))
                    MarkNotOptimized(method);
                if (rs.MethodBody.TryGetValue(methodKey, out var bodyRules) && bodyRules.Count > 0)
                {
                    ReplaceMethodBody(method, module, bodyRules[0]);
                }
                else if (rs.Probe.TryGetValue(methodKey, out var probeRules) && probeRules.Count > 0)
                {
                    InstrumentMethod(method, module, probeRules[0]);
                }
                else if (rs.Mark.TryGetValue(methodKey, out var markRules) && markRules.Count > 0)
                {
                    MarkMethod(method, module, markRules[0]);
                }
            }

            foreach (var method in type.Methods)
            {
                if (method.Body != null)
                    RewriteInstructions(method, module, rs);
            }
        }
    }

    private void ReplaceMethodBody(MethodDefinition method, ModuleDefinition module, HookRule rule)
    {
        if (method.Body == null) return;

        var il = method.Body.GetILProcessor();

        while (method.Body.Instructions.Count > 0)
            il.Remove(method.Body.Instructions[0]);

        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();

        int paramCount = method.HasThis ? method.Parameters.Count + 1 : method.Parameters.Count;
        for (int i = 0; i < paramCount; i++)
            il.Append(il.Create(GetLdarg(i)));

        var replacementRef = ResolveMethodBodyReplacement(rule, module, method);
        if (replacementRef == null)
            return;

        il.Append(il.Create(OpCodes.Call, replacementRef));
        il.Append(il.Create(OpCodes.Ret));

        _rewriteCount++;
    }

    //InstrumentMethod 在方法入口与每个出口插桩 原实现保持不变
    //开始时间存进新加的局部变量而不是静态字段 这样同一线程上的嵌套调用互不覆盖
    //正因如此才能同时观测 ProcessChunk 与其内部调用的 FillFromNoise 这类包含关系
    private void InstrumentMethod(MethodDefinition method, ModuleDefinition module, HookRule rule)
    {
        if (method.Body == null || method.IsAbstract || method.IsPInvokeImpl)
            return;

        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return;
        var reporterType = resolved.Value.Type;

        var begin = FindStaticMethod(reporterType, "Begin", 0);
        var end = FindStaticMethod(reporterType, rule.ReplacementMethod, 2);
        if (begin == null || end == null)
            return;

        var body = method.Body;
        var returns = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        if (returns.Count == 0)
            return;

        var il = body.GetILProcessor();
        var beginRef = module.ImportReference(begin);
        var endRef = module.ImportReference(end);

        var stamp = new VariableDefinition(module.TypeSystem.Int64);
        body.Variables.Add(stamp);
        body.InitLocals = true;

        var first = body.Instructions[0];
        var beginCall = il.Create(OpCodes.Call, beginRef);
        var storeStamp = il.Create(OpCodes.Stloc, stamp);
        MoveBoundaries(body, first, beginCall);
        il.InsertBefore(first, beginCall);
        il.InsertBefore(first, storeStamp);

        var label = rule.Label ?? $"{method.DeclaringType?.Name}::{method.Name}";
        //按参数分桶时标签在出口处才拼出来 同一个方法因此能分成多个观测点
        var argumentIndex = rule.LabelArgumentIndex;
        var deriveLabel = argumentIndex is int index && index >= 0 && index < method.Parameters.Count;
        var toStringRef = deriveLabel ? module.ImportReference(ObjectToString) : null;
        var concatRef = deriveLabel ? module.ImportReference(StringConcat) : null;
        foreach (var ret in returns)
        {
            var pushLabel = deriveLabel
                ? CreateArgumentLabel(il, module, label, method.Parameters[argumentIndex!.Value], toStringRef!, concatRef!)
                : [il.Create(OpCodes.Ldstr, label)];
            var pushStamp = il.Create(OpCodes.Ldloc, stamp);
            var report = il.Create(OpCodes.Call, endRef);

            RedirectBranches(body, ret, pushLabel[0]);
            MoveBoundaries(body, ret, pushLabel[0]);
            foreach (var instruction in pushLabel)
                il.InsertBefore(ret, instruction);
            il.InsertBefore(ret, pushStamp);
            il.InsertBefore(ret, report);
        }

        _rewriteCount++;
    }

    //CreateArgumentLabel 生成"前缀 + 参数文本"的标签指令序列
    //值类型要先装箱才能取 ToString 引用类型不用 这里按类型判断省一条指令
    private static Instruction[] CreateArgumentLabel(ILProcessor il, ModuleDefinition module, string prefix, ParameterDefinition parameter, MethodReference toString, MethodReference concat)
    {
        var instructions = new List<Instruction>
        {
            il.Create(OpCodes.Ldstr, prefix),
            il.Create(OpCodes.Ldarg, parameter),
        };
        if (IsValueType(parameter.ParameterType))
            instructions.Add(il.Create(OpCodes.Box, module.ImportReference(parameter.ParameterType)));
        instructions.Add(il.Create(OpCodes.Callvirt, toString));
        instructions.Add(il.Create(OpCodes.Call, concat));
        return instructions.ToArray();
    }

    //IsValueType 判断参数类型是否为值类型 类型引用解析不了时按引用类型处理
    private static bool IsValueType(TypeReference type)
    {
        try
        {
            return type.IsValueType;
        }
        catch
        {
            return false;
        }
    }

    //RedirectBranches 把所有跳向锚点的分支改指到新的落点
    //方法体以 foreach 结尾时 C# 会让 leave 直接跳向 ret
    //不重定向的话 leave 会跨过插在 ret 前的上报代码 记录全部丢失
    private static void RedirectBranches(Mono.Cecil.Cil.MethodBody body, Instruction anchor, Instruction newTarget)
    {
        foreach (var instruction in body.Instructions)
        {
            switch (instruction.Operand)
            {
                case Instruction target when ReferenceEquals(target, anchor):
                    instruction.Operand = newTarget;
                    break;
                case Instruction[] targets:
                    for (var i = 0; i < targets.Length; i++)
                    {
                        if (ReferenceEquals(targets[i], anchor))
                            targets[i] = newTarget;
                    }
                    break;
            }
        }
    }

    //MoveBoundaries 把指向锚点的异常处理边界前移到即将插入的第一条指令
    //C# 给 foreach 生成的 try/finally 其 HandlerEnd 常常正指向 ret
    //不前移的话插入的代码会落进 finally 范围 校验器直接判非法程序
    private static void MoveBoundaries(Mono.Cecil.Cil.MethodBody body, Instruction anchor, Instruction newFirst)
    {
        foreach (var handler in body.ExceptionHandlers)
        {
            if (handler.TryEnd == anchor)
                handler.TryEnd = newFirst;
            if (handler.HandlerEnd == anchor)
                handler.HandlerEnd = newFirst;
        }
    }

    //FindStaticMethod 在探针类里按名字与参数个数找静态方法
    private static MethodInfo? FindStaticMethod(Type type, string name, int paramCount)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == paramCount);
    }

    //MarkMethod 只在方法入口插一次上报 原实现不变
    //用来捕获"某件事发生了"的时刻 例如首个玩家进入游戏 但不计耗时
    private void MarkMethod(MethodDefinition method, ModuleDefinition module, HookRule rule)
    {
        if (method.Body == null || method.IsAbstract || method.IsPInvokeImpl)
            return;

        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return;

        var mark = FindStaticMethod(resolved.Value.Type, rule.ReplacementMethod, 1);
        if (mark == null)
            return;

        var body = method.Body;
        if (body.Instructions.Count == 0)
            return;

        var il = body.GetILProcessor();
        var markRef = module.ImportReference(mark);
        var label = rule.Label ?? $"{method.DeclaringType?.Name}::{method.Name}";

        var first = body.Instructions[0];
        var pushLabel = il.Create(OpCodes.Ldstr, label);
        var call = il.Create(OpCodes.Call, markRef);
        MoveBoundaries(body, first, pushLabel);
        il.InsertBefore(first, pushLabel);
        il.InsertBefore(first, call);

        _rewriteCount++;
    }

    private void RewriteInstructions(MethodDefinition method, ModuleDefinition module, RuleSet rs)
    {
        var il = method.Body.GetILProcessor();
        var instructions = method.Body.Instructions.ToList();
        //局部变量与常量这两类的锚点就在宿主自己体内 规则的 OriginalType/OriginalMethod 记的就是宿主 键与这里同源
        var hostKey = $"{method.DeclaringType?.FullName}::{method.Name}";
        //ordinal 数的是宿主方法内第几处 换方法就重新数
        _ordinalCounts.Clear();

        for (int i = 0; i < instructions.Count; i++)
        {
            var instr = instructions[i];
            var handled = false;

            if (!handled && (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt))
                handled = TryRewriteCallSite(instr, il, module, method, rs.CallSite);

            if (!handled && instr.OpCode == OpCodes.Newobj)
                handled = TryRewriteNewObj(instr, il, module, method, rs.NewObj);

            if (!handled && (instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Ldsfld))
                handled = TryRewriteFieldRead(instr, il, module, method, rs.FieldRead);

            if (!handled && (instr.OpCode == OpCodes.Stfld || instr.OpCode == OpCodes.Stsfld))
                handled = TryRewriteFieldWrite(instr, il, module, method, rs.FieldWrite);

            if (!handled && (instr.OpCode == OpCodes.Isinst || instr.OpCode == OpCodes.Castclass))
                handled = TryRewriteTypeCheck(instr, il, module, method, rs.TypeCheck);

            if (!handled && (instr.OpCode == OpCodes.Box || instr.OpCode == OpCodes.Unbox_Any))
                handled = TryRewriteBox(instr, il, module, method, rs.Box);

            if (!handled && (instr.OpCode == OpCodes.Ldftn || instr.OpCode == OpCodes.Ldvirtftn))
                handled = TryRewriteFunctionPointer(instr, il, module, method, rs.FunctionPointer);

            if (!handled && (rs.LocalRead.Count > 0 || rs.LocalWrite.Count > 0)
                && TryGetLocalIndex(instr, out var localIndex, out var isWrite))
                handled = TryRewriteLocal(instr, il, module, method, hostKey, localIndex, isWrite,
                    isWrite ? rs.LocalWrite : rs.LocalRead);

            if (!handled && rs.Constant.Count > 0 && TryGetConstantValue(instr, out var constant))
                handled = TryRewriteConstant(instr, il, module, method, hostKey, constant, rs.Constant);

            if (handled)
                _rewriteCount++;
        }
    }

    //FindRuleForHost 在候选规则里挑第一条作用于当前宿主方法的
    //InType/InMethod 为空表示不限 两条都为空时退化成原来的"取第一条"
    //挑中之后再过一道 Ordinal 没设就放行 设了只放行第 N 处 其余返回 null 当作这条指令没规则
    private HookRule? FindRuleForHost(List<HookRule> rules, MethodDefinition host)
    {
        foreach (var rule in rules)
        {
            if (rule.InType is not null && host.DeclaringType?.FullName != rule.InType)
                continue;
            if (rule.InMethod is not null && host.Name != rule.InMethod)
                continue;
            return AcceptOrdinal(rule) ? rule : null;
        }
        return null;
    }

    //AcceptOrdinal 这一处匹配要不要落地 顺带把它记进计数
    //计数放在替换成败之前 匹配上就算一处 与那一处最后改没改成无关
    //这条跟 Mixin 的 @At(ordinal) 对齐 都是数方法内第几处出现
    private bool AcceptOrdinal(HookRule rule)
    {
        var seen = _ordinalCounts.TryGetValue(rule, out var count) ? count : 0;
        _ordinalCounts[rule] = seen + 1;
        return rule.Ordinal is null || rule.Ordinal.Value == seen;
    }

    //HostArgCount 宿主方法传给回调的参数个数 实例方法的 this 算一个
    //插入模式按这个数对齐 替换模式按被顶替那条指令的实参个数对齐 两者不是一回事
    private static int HostArgCount(MethodDefinition host)
        => host.HasThis ? host.Parameters.Count + 1 : host.Parameters.Count;

    //ApplyPlacement 按规则的落位方式把替换调用落到锚点指令上
    //Replace 顶替锚点 原调用不再执行 要不要补回由回调自己决定
    //Before/After 保留锚点 只在它前后多调一次
    private static void ApplyPlacement(HookRule rule, Instruction anchor, ILProcessor il, MethodDefinition host, MethodReference replacement)
    {
        if (rule.Placement == HookPlacement.Replace)
        {
            il.Replace(anchor, il.Create(OpCodes.Call, replacement));
            return;
        }

        //先把这一串指令建好再插 边界前移要知道第一条是哪条
        var sequence = new List<Instruction>();
        var count = HostArgCount(host);
        for (var i = 0; i < count; i++)
            sequence.Add(CreateLdarg(il, host, i));
        sequence.Add(il.Create(OpCodes.Call, replacement));

        if (rule.Placement == HookPlacement.Before)
        {
            //异常处理边界若正指向锚点 要让给新指令 否则插入的代码落在 try 范围之外
            if (host.Body is not null)
                MoveBoundaries(host.Body, anchor, sequence[0]);
            foreach (var instruction in sequence)
                il.InsertBefore(anchor, instruction);
        }
        else
        {
            //倒着插 每次都是贴在锚点后面 顺序才对
            for (var i = sequence.Count - 1; i >= 0; i--)
                il.InsertAfter(anchor, sequence[i]);
        }
    }

    //CreateLdarg 生成读取第 index 个参数的指令 index 含实例方法的 this
    //ldarg.0-3 是短指令 超出就要带参数定义作操作数 只给 opcode 生成的是非法 IL
    private static Instruction CreateLdarg(ILProcessor il, MethodDefinition host, int index)
    {
        return index switch
        {
            0 => il.Create(OpCodes.Ldarg_0),
            1 => il.Create(OpCodes.Ldarg_1),
            2 => il.Create(OpCodes.Ldarg_2),
            3 => il.Create(OpCodes.Ldarg_3),
            _ => il.Create(OpCodes.Ldarg, host.Parameters[index - (host.HasThis ? 1 : 0)])
        };
    }

    private bool TryRewriteCallSite(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not MethodReference target)
            return false;

        var key = $"{target.DeclaringType?.FullName}::{target.Name}";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        //实例方法的 this 都在栈上 判据只能是目标方法有没有 this
        //sealed 类型上的非虚方法编译器生成的是 call 不是 callvirt 只看 opcode 会把参数个数算少一位 替换后栈上多留一个 this
        var isInstanceCall = target.HasThis;
        var replacementRef = ResolveCallSiteReplacement(rule, module, target, isInstanceCall);
        if (replacementRef == null)
            return false;

        //替换要凑够被调方法的实参 插入只读宿主方法的参数 两者不是一回事
        var expectedParamCount = rule.Placement == HookPlacement.Replace
            ? (isInstanceCall ? target.Parameters.Count + 1 : target.Parameters.Count)
            : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteNewObj(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not MethodReference ctor)
            return false;

        var key = $"{ctor.DeclaringType?.FullName}::.ctor";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var replacementRef = ResolveNewObjReplacement(rule, module, ctor);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? ctor.Parameters.Count : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteFieldRead(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not FieldReference field)
            return false;

        var key = $"{field.DeclaringType?.FullName}::{field.Name}";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var isStatic = instr.OpCode == OpCodes.Ldsfld;
        var replacementRef = ResolveFieldReplacement(rule, module, isStatic ? 0 : 1);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? (isStatic ? 0 : 1) : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteFieldWrite(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not FieldReference field)
            return false;

        var key = $"{field.DeclaringType?.FullName}::{field.Name}";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var isStatic = instr.OpCode == OpCodes.Stsfld;
        var replacementRef = ResolveFieldReplacement(rule, module, isStatic ? 1 : 2);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? (isStatic ? 1 : 2) : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteTypeCheck(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not TypeReference typeRef)
            return false;

        var key = $"{typeRef.FullName}::check";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var replacementRef = ResolveTypeCheckReplacement(rule, module);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? 1 : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteBox(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not TypeReference typeRef)
            return false;

        var key = $"{typeRef.FullName}::{(instr.OpCode == OpCodes.Box ? "box" : "unbox")}";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var replacementRef = ResolveBoxReplacement(rule, module);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? 1 : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    private bool TryRewriteFunctionPointer(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host, Dictionary<string, List<HookRule>> rules)
    {
        if (instr.Operand is not MethodReference target)
            return false;

        var key = $"{target.DeclaringType?.FullName}::{target.Name}";
        if (!rules.TryGetValue(key, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindRuleForHost(ruleList, host);
        if (rule is null)
            return false;

        var isVirtual = instr.OpCode == OpCodes.Ldvirtftn;
        var replacementRef = ResolveFuncPtrReplacement(rule, module, isVirtual);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? (isVirtual ? 1 : 0) : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    //TryRewriteLocal 处理局部变量的读或写
    //读是往栈上推一个值 替换方法要返回它 所以零参
    //写是从栈上取一个值 替换方法要接收它 所以一参
    private bool TryRewriteLocal(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host,
        string hostKey, int localIndex, bool isWrite, Dictionary<string, List<HookRule>> rules)
    {
        if (!rules.TryGetValue(hostKey, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindLocalRule(ruleList, host, localIndex);
        if (rule is null)
            return false;

        var replaceArgCount = isWrite ? 1 : 0;
        var replacementRef = ResolveSiteReplacement(rule, module, replaceArgCount);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? replaceArgCount : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    //TryRewriteConstant 处理常量加载 常量指令往栈上推一个值 替换方法零参
    private bool TryRewriteConstant(Instruction instr, ILProcessor il, ModuleDefinition module, MethodDefinition host,
        string hostKey, object? constant, Dictionary<string, List<HookRule>> rules)
    {
        if (!rules.TryGetValue(hostKey, out var ruleList) || ruleList.Count == 0)
            return false;

        var rule = FindConstantRule(ruleList, host, constant);
        if (rule is null)
            return false;

        var replacementRef = ResolveSiteReplacement(rule, module, 0);
        if (replacementRef == null)
            return false;

        var expectedParamCount = rule.Placement == HookPlacement.Replace ? 0 : HostArgCount(host);
        if (replacementRef.Parameters.Count != expectedParamCount)
            return false;

        ApplyPlacement(rule, instr, il, host, replacementRef);
        return true;
    }

    //FindLocalRule 挑第一条槽位对得上且宿主匹配的局部变量规则
    //Ordinal 的过法跟 FindRuleForHost 一样 都是匹配上就算一处
    private HookRule? FindLocalRule(List<HookRule> rules, MethodDefinition host, int localIndex)
    {
        foreach (var rule in rules)
        {
            if (rule.LocalIndex != localIndex) continue;
            if (rule.InType is not null && host.DeclaringType?.FullName != rule.InType) continue;
            if (rule.InMethod is not null && host.Name != rule.InMethod) continue;
            return AcceptOrdinal(rule) ? rule : null;
        }
        return null;
    }

    //FindConstantRule 挑第一条常量值对得上且宿主匹配的规则
    private HookRule? FindConstantRule(List<HookRule> rules, MethodDefinition host, object? constant)
    {
        foreach (var rule in rules)
        {
            if (!Equals(rule.ConstantValue, constant)) continue;
            if (rule.InType is not null && host.DeclaringType?.FullName != rule.InType) continue;
            if (rule.InMethod is not null && host.Name != rule.InMethod) continue;
            return AcceptOrdinal(rule) ? rule : null;
        }
        return null;
    }

    //TryGetLocalIndex 取局部变量指令的槽位 0 基 顺带分辨读还是写
    //ldloc.0 这类短形式 Cecil 不填操作数 槽位要从 opcode 反推
    private static bool TryGetLocalIndex(Instruction instr, out int index, out bool isWrite)
    {
        var code = instr.OpCode.Code;
        if (code >= Code.Ldloc_0 && code <= Code.Ldloc_3)
        {
            index = code - Code.Ldloc_0;
            isWrite = false;
            return true;
        }
        if (code >= Code.Stloc_0 && code <= Code.Stloc_3)
        {
            index = code - Code.Stloc_0;
            isWrite = true;
            return true;
        }
        if ((code is Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S)
            && instr.Operand is VariableDefinition variable)
        {
            index = variable.Index;
            isWrite = code is Code.Stloc or Code.Stloc_S;
            return true;
        }
        index = -1;
        isWrite = false;
        return false;
    }

    //TryGetConstantValue 取常量加载指令的值 非常量指令返回 false
    //ldc.i4.0 这类短形式同样不带操作数 值要从 opcode 反推
    private static bool TryGetConstantValue(Instruction instr, out object? value)
    {
        switch (instr.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S:
            case Code.Ldc_I4: value = Convert.ToInt32(instr.Operand); return true;
            case Code.Ldc_I8: value = Convert.ToInt64(instr.Operand); return true;
            case Code.Ldc_R4: value = Convert.ToSingle(instr.Operand); return true;
            case Code.Ldc_R8: value = Convert.ToDouble(instr.Operand); return true;
            case Code.Ldstr: value = instr.Operand as string; return true;
            default: value = null; return false;
        }
    }

    //ResolveSiteReplacement 按期望参数个数解析替换方法
    //局部变量与常量的实参个数由指令的栈效果定 推一个值就是零参 取一个值就是一参
    private MethodReference? ResolveSiteReplacement(HookRule rule, ModuleDefinition targetModule, int expectedParamCount)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, expectedParamCount);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveCallSiteReplacement(HookRule rule, ModuleDefinition targetModule, MethodReference originalCall, bool isInstanceCall)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var expectedParamCount = isInstanceCall ? originalCall.Parameters.Count + 1 : originalCall.Parameters.Count;
        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, expectedParamCount);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveMethodBodyReplacement(HookRule rule, ModuleDefinition targetModule, MethodDefinition originalMethod)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var expectedParamCount = originalMethod.HasThis ? originalMethod.Parameters.Count + 1 : originalMethod.Parameters.Count;
        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, expectedParamCount);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveNewObjReplacement(HookRule rule, ModuleDefinition targetModule, MethodReference originalCtor)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, originalCtor.Parameters.Count);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveFieldReplacement(HookRule rule, ModuleDefinition targetModule, int expectedParamCount)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, expectedParamCount);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveTypeCheckReplacement(HookRule rule, ModuleDefinition targetModule)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, 1);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveBoxReplacement(HookRule rule, ModuleDefinition targetModule)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, 1);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    private MethodReference? ResolveFuncPtrReplacement(HookRule rule, ModuleDefinition targetModule, bool isVirtual)
    {
        var resolved = ResolveReplacement(rule);
        if (resolved is null)
            return null;
        var (replacementAsm, replacementType) = resolved.Value;

        var expectedParamCount = isVirtual ? 1 : 0;
        var replacementMethodInfo = FindReplacementMethod(replacementType, rule.ReplacementMethod, expectedParamCount);
        if (replacementMethodInfo == null)
            return null;

        return BuildMethodReference(replacementMethodInfo, rule.ReplacementMethod, replacementType, replacementAsm, targetModule);
    }

    //ResolveReplacement 取替换方程序集与替换类型
    //已登记的直接用 只登记了加载器的现拉一次并留档 两者都没有或类型找不到返回 null
    private (Assembly Asm, Type Type)? ResolveReplacement(HookRule rule)
    {
        if (!_replacementAssemblies.TryGetValue(rule.ReplacementAssembly, out var asm))
        {
            if (!_replacementSources.TryGetValue(rule.ReplacementAssembly, out var loader))
                return null;

            asm = loader();
            if (asm is null)
                return null;
            _replacementAssemblies[rule.ReplacementAssembly] = asm;
        }

        var type = asm.GetType(rule.ReplacementTypeName);
        return type is null ? null : (asm, type);
    }

    private MethodInfo? FindReplacementMethod(Type replacementType, string methodName, int expectedParamCount)
    {
        var result = replacementType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == expectedParamCount);
        if (result != null)
            return result;

        return replacementType.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    private MethodReference BuildMethodReference(MethodInfo replacementMethodInfo, string methodName, Type replacementType, Assembly replacementAsm, ModuleDefinition targetModule)
    {
        var asmRef = targetModule.AssemblyReferences.FirstOrDefault(a => a.FullName == replacementAsm.FullName);
        if (asmRef == null)
        {
            asmRef = new AssemblyNameReference(replacementAsm.GetName().Name, replacementAsm.GetName().Version);
            targetModule.AssemblyReferences.Add(asmRef);
        }

        var typeRef = new TypeReference(replacementType.Namespace, replacementType.Name, targetModule, asmRef);

        var paramTypes = replacementMethodInfo.GetParameters()
            .Select(p => ImportType(p.ParameterType, targetModule, asmRef))
            .ToList();

        var returnType = ImportType(replacementMethodInfo.ReturnType, targetModule, asmRef);

        var methodRef = new MethodReference(methodName, returnType, typeRef);
        foreach (var param in paramTypes)
            methodRef.Parameters.Add(new ParameterDefinition(param));

        return methodRef;
    }

    private static OpCode GetLdarg(int index)
    {
        return index switch
        {
            0 => OpCodes.Ldarg_0,
            1 => OpCodes.Ldarg_1,
            2 => OpCodes.Ldarg_2,
            3 => OpCodes.Ldarg_3,
            _ => OpCodes.Ldarg
        };
    }

    private TypeReference ImportType(Type type, ModuleDefinition module, AssemblyNameReference asmRef)
    {
        if (type == typeof(void)) return module.TypeSystem.Void;
        if (type == typeof(string)) return module.TypeSystem.String;
        if (type == typeof(int)) return module.TypeSystem.Int32;
        if (type == typeof(bool)) return module.TypeSystem.Boolean;
        if (type == typeof(long)) return module.TypeSystem.Int64;
        if (type == typeof(byte)) return module.TypeSystem.Byte;
        if (type == typeof(byte[])) return new ArrayType(module.TypeSystem.Byte);
        if (type == typeof(object)) return module.TypeSystem.Object;
        if (type == typeof(double)) return module.TypeSystem.Double;
        if (type == typeof(float)) return module.TypeSystem.Single;
        if (type == typeof(char)) return module.TypeSystem.Char;
        if (type == typeof(short)) return module.TypeSystem.Int16;
        if (type == typeof(ushort)) return module.TypeSystem.UInt16;
        if (type == typeof(uint)) return module.TypeSystem.UInt32;
        if (type == typeof(ulong)) return module.TypeSystem.UInt64;
        if (type == typeof(IntPtr)) return module.TypeSystem.IntPtr;
        if (type == typeof(UIntPtr)) return module.TypeSystem.UIntPtr;

        try
        {
            var imported = module.ImportReference(type);
            return imported;
        }
        catch
        {
            if (type.IsGenericType)
            {
                var genericDef = type.GetGenericTypeDefinition();
                var elemRef = new TypeReference(genericDef.Namespace, genericDef.Name, module, asmRef);
                var genInst = new GenericInstanceType(elemRef);
                foreach (var arg in type.GetGenericArguments())
                    genInst.GenericArguments.Add(ImportType(arg, module, asmRef));
                return genInst;
            }

            if (type.IsArray)
            {
                var elementType = ImportType(type.GetElementType()!, module, asmRef);
                return new ArrayType(elementType);
            }

            return new TypeReference(type.Namespace, type.Name, module, asmRef);
        }
    }
}
