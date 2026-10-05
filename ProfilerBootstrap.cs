using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lead.Hook;

//ProfilerBootstrap 准备「让目标进程挂上原生 profiler」所需的那组信息
//库只给信息 设环境变量与启动进程由调用者做
//profiler 的三个环境变量 CLR 只在进程启动时读一次 进程内改自己的环境变量没有任何效果
//所以想让一个已经在跑的进程挂上 只能由它的父进程在启动它之前把变量设好
public static class ProfilerBootstrap
{
    //Clsid profiler 的 COM 类标识 必须与原生库里的 GUID 完全一致
    //拼错一位的表现是静默不挂载 没有任何报错 所以这个值不交给调用者自己写
    public const string Clsid = "{7A2E4C1B-9D3F-4E58-A6B0-1C5D8F2A3E70}";

    private static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(0);

    private static string? _cachedPath;
    private static bool _probed;

    //IsSupported 当前平台有没有对应的原生库
    //只有 x64 的 windows linux macos 三份 其余平台一律为假
    public static bool IsSupported => FileNameForCurrentPlatform() is not null;

    //NativeLibraryPath 探测到的原生库绝对路径 找不到为 null
    //从包引用的正常布局下会自动命中 nuget 铺好的那份
    public static string? NativeLibraryPath
    {
        get
        {
            if (!_probed)
            {
                _cachedPath = Probe();
                _probed = true;
            }
            return _cachedPath;
        }
    }

    //EnvironmentVariables 要注入目标进程的三个变量 原生库缺失时为空
    public static IReadOnlyDictionary<string, string> EnvironmentVariables
        => NativeLibraryPath is { } path
            ? new Dictionary<string, string>(3)
            {
                ["CORECLR_ENABLE_PROFILING"] = "1",
                ["CORECLR_PROFILER"] = Clsid,
                ["CORECLR_PROFILER_PATH"] = path,
            }
            : None;

    //Apply 把变量写进一份进程启动配置 返回是否成功
    //调用者接着起这个进程就行 子进程会继承过去
    //single-file 发布与手工部署下宿主目录对不上 这两种情况用 nativeLibraryPath 显式指定
    public static bool Apply(ProcessStartInfo startInfo, string? nativeLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var path = nativeLibraryPath ?? NativeLibraryPath;
        if (path is null)
            return false;

        startInfo.Environment["CORECLR_ENABLE_PROFILING"] = "1";
        startInfo.Environment["CORECLR_PROFILER"] = Clsid;
        startInfo.Environment["CORECLR_PROFILER_PATH"] = path;
        return true;
    }

    //Describe 一句话说明当前状态与代价 直接进日志
    //挂上之后整个进程放弃 ReadyToRun 启动会变慢 这一步是 ReJIT 的必要前提 调用者该知道
    public static string Describe()
    {
        if (NativeLibraryPath is not { } path)
        {
            return IsSupported
                ? $"profiler library not found for {RuntimeInformation.RuntimeIdentifier}, pass the path explicitly"
                : $"profiler is not supported on {RuntimeInformation.RuntimeIdentifier}";
        }

        return $"profiler ready ({RuntimeInformation.RuntimeIdentifier}) at {path}; "
            + "the target process runs without ReadyToRun and starts up slower";
    }

    private static string? Probe()
    {
        if (FileNameForCurrentPlatform() is not { } fileName)
            return null;

        foreach (var directory in SearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    //SearchDirectories 按可靠性从高到低给出候选目录
    //宿主算好的那份最准 RID 回退与 nuget 缓存布局它都处理过了
    private static IEnumerable<string> SearchDirectories()
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string raw
            && !string.IsNullOrWhiteSpace(raw))
        {
            foreach (var directory in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return directory;
        }

        //宿主那份拿不到时退回手拼 直接部署与开发期走的是这里
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        yield return baseDirectory;
    }

    //FileNameForCurrentPlatform 当前平台对应的原生库文件名 不支持的平台给 null
    //cdylib 在 windows 上不带前缀 linux 是 lib*.so macos 是 lib*.dylib
    private static string? FileNameForCurrentPlatform()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null;

        if (OperatingSystem.IsWindows())
            return "lead_hook_native.dll";
        if (OperatingSystem.IsLinux())
            return "liblead_hook_native.so";
        if (OperatingSystem.IsMacOS())
            return "liblead_hook_native.dylib";

        return null;
    }
}
