using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace SprocketTranslation;

internal static class PluginInfo
{
    public const string PLUGIN_GUID = "dev.sprocket.translation";
    public const string PLUGIN_NAME = "SprocketTranslation";
    public const string PLUGIN_VERSION = "1.4.3";
    public const string AUTHOR_ZH = "七夜已逝";
    public const string AUTHOR_EN = "Sevenight";
}

/// <summary>
/// 纯 Harmony 汉化插件：只 Hook 文本 setter，不注入任何 IL2CPP 类型，
/// 因此兼容 Unity 6 的 BepInEx patch（需要 dobby.dll + 跳过钩子的 Il2CppInterop）。
///
/// 词条来源：正式包词条以加密形式内嵌在 DLL 中（deflate+XOR），不再随游戏目录明文分发；
/// 外部 “原文=译文” 格式的 .txt 仍会被全部加载，并覆盖同名内嵌词条（自定义词条：原文=译文）。
/// 文件位置（任选其一，.txt 会被全部加载）：
///   &lt;游戏根目录&gt;\Translations\
///   &lt;游戏根目录&gt;\BepInEx\Translations\
///
/// 中文字体：从 TTF 文件（Alibaba-PuHuiTi-Medium.ttf）创建，运行时生成 TMP 字体
/// （atlas 完整，中文可正常渲染），设置 TMP 默认字体 + 全局回退。
/// </summary>
[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public sealed class Plugin : BasePlugin
{
    private static readonly Dictionary<string, string> Translations = new(StringComparer.Ordinal);
    private static readonly HashSet<MethodBase> PatchedMethods = new();
    // 类型级编译委托缓存（按运行时类型）：文本属性读写不再每次走 PropertyInfo/MethodInfo.Invoke 反射
    private static readonly Dictionary<Type, Action<object, string>> TextWriteCache = new();
    private static readonly Dictionary<Type, Func<object, string>> TextReadCache = new();
    private static ManualLogSource LogSource = null!;
    private static bool applyingTranslation;

    // 通配符（*）词条缓存：原文含 * 的翻译 key → 预编译正则
    private static readonly List<KeyValuePair<string, Regex>> WildcardKeys = new();
    private static bool wildcardReady;

    // 格式 SetText 负缓存：某格式串（如 "Weight: {0}"）首次模板未命中 → 之后跳过模板匹配（字典静态）
    private static readonly HashSet<string> NoMatchFormats = new(StringComparer.Ordinal);

    // 钩子开销仪表：统计翻译钩子累计耗时，超阈值时日志提示（诊断零件/蓝图界面是否由我们拖慢）
    private static long hookCostMs;
    private static int hookCostCalls;
    private static bool hookCostLogged;

    private static object? cjkTmpFontAsset; // TMPro.TMP_FontAsset（TTF 创建的中文字体）

    // ---------- 设置 / 文本提取 ----------
    private static ConfigEntry<bool>? CfgTranslationEnabled;
    private static ConfigEntry<bool>? CfgExtractionMode;
    private static ConfigEntry<bool>? CfgFormatHooks;
    private static ConfigEntry<bool>? CfgReplaceDefaultFont;
    private static ConfigEntry<string>? CfgFontFile;
    private static ConfigEntry<string>? CfgFallbackFontNames;
    private static bool translationEnabled = true;
    private static bool extractionMode;
    private static string? extractionFilePath;
    private static readonly HashSet<string> ExtractedKeys = new(StringComparer.Ordinal);
    private static object? pinnedStream;      // 防止流被 IL2CPP GC 回收
    private static object? pinnedRequest;     // 防止 AssetBundleRequest 被 IL2CPP GC 回收

    // TTF 字体加载状态（Unity API 必须在主线程，故由 Postfix 触发）
    private static string? ttfFontPath;
    private static string? preferredOsFontName;
    private static bool ttfAttempted;

    private Harmony harmony = null!;

    /// <summary>快速类型查找：遍历已加载程序集用 GetType（不触发 GetTypes() 全量扫描，避免
    /// interop 程序集上 AccessTools.TypeByName 的 TypeLoadException 洪流）。</summary>
    private static Type? SafeType(string fullName)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null)
                    return t;
            }
        }
        catch { }
        return null;
    }

    public override void Load()
    {
        try
        {
            LoadInternal();
        }
        catch (Exception ex)
        {
            // 任何加载错误都不能让游戏闪退：记录日志并继续
            Log.LogError($"Plugin load error (ignored, continuing): {ex}");
        }
    }

    private void LoadInternal()
    {
        long loadStartMs = Environment.TickCount64;
        LogSource = Log;
        harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        Log.LogInfo($"Author: {PluginInfo.AUTHOR_EN}");

        // 设置（cfg 文件控制）
        CfgTranslationEnabled = Config.Bind("General", "TranslationEnabled", true,
            "启用文本翻译。关闭后显示游戏原文，但仍加载中文字体（适合只想支持中文、不想汉化的人）。");
        CfgExtractionMode = Config.Bind("General", "ExtractionMode", false,
            "文本提取模式（需翻译开启）：把未翻译原文（数字已替换为 {{A}} 占位符）追加到 Translations\\_ExtractedText.txt。");
        CfgFormatHooks = Config.Bind("General", "FormatHooksEnabled", true,
            "是否钩住 TMP SetText(string,float/bool...) 格式重载（翻译 Weight: {0} 这类动态文本）。\n" +
            "若怀疑蓝图/零件界面卡顿由翻译钩子引起，可设为 false 单独排除（普通文本翻译不受影响）。");
        CfgFontFile = Config.Bind("Font", "FontFile", "Alibaba-PuHuiTi-Medium.ttf",
            "中文字体来源（默认 Alibaba-PuHuiTi-Medium.ttf=补丁 Fonts 文件夹内置的阿里巴巴普惠体，随补丁分发，正常无需改动）。\n" +
            "可填：Fonts/游戏根目录/BepInEx 下的 TTF 文件名或路径、Unity 6 TMP 字体资源包名（如 guzhanhei 6000 / ark-pixel 6000）、\n" +
            "或系统字体名（如 SimHei；机器支持时可省字体文件，但动态字体在部分机器会缺字变方框，不推荐）。");
        CfgFallbackFontNames = Config.Bind("Font", "FallbackFontNames", "Microsoft YaHei,SimHei,SimSun,NSimSun,Noto Sans SC",
            "TTF/bundle 加载失败时依次尝试的系统字体名（逗号分隔）。");
        CfgReplaceDefaultFont = Config.Bind("Font", "ReplaceDefaultFont", false,
            "是否把 TMP 默认字体替换为汉化字体（全局生效）。\n" +
            "false=仅作回退（推荐）：英文原文保持官方字体外观，只有缺字的中文才用汉化字体，避免小字号变粗/发虚；\n" +
            "true=替换默认：所有文本都用汉化字体渲染（新文本组件直接使用汉化字体）。");
        translationEnabled = CfgTranslationEnabled.Value;
        extractionMode = CfgExtractionMode.Value;
        if (extractionMode)
            InitExtractionFile();

        TryLoadCjkFont();

        int count = LoadTranslations();
        Log.LogInfo($"Loaded {count} translations.");
        BuildWildcardCache();

        int hooked = PatchTextSetters();
        Log.LogInfo($"Hooked {hooked} text entry points.");

        // Load 运行在 Unity 主线程：立即加载中文字体（原为首次设置文本时触发，改为启动即加载更稳定）
        try
        {
            EnsureMainThreadFontStep();
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"Font load error at startup (will retry on first text set): {ex.Message}");
        }

        // 字体创建在词条加载之后：此处把全部词条用字一次性烘进动态图集（修复部件名生僻字方框）
        PopulateFontFromTranslations();

        if (count == 0)
        {
            Log.LogWarning("No translations loaded (embedded resource empty and no external .txt found).");
        }

        Log.LogInfo($"[timing] Plugin load done: total {Environment.TickCount64 - loadStartMs}ms " +
                    $"(translation files {count}, hooks {PatchedMethods.Count}, neg-cache formats {NoMatchFormats.Count}). " +
                    "Compare with the same game boot without the plugin to measure mod cost.");
    }

    // ---------------- CJK 字体（TTF，主线程创建） ----------------

    private void TryLoadCjkFont()
    {
        var fontFile = CfgFontFile?.Value?.Trim();
        if (string.IsNullOrEmpty(fontFile))
            fontFile = "Alibaba-PuHuiTi-Medium.ttf";

        // 依次尝试：原样（绝对/相对）、游戏根目录、游戏根目录\Fonts、BepInEx 根目录
        var candidates = new List<string>();
        if (Path.IsPathRooted(fontFile))
        {
            candidates.Add(fontFile);
        }
        else
        {
            candidates.Add(fontFile);
            candidates.Add(Path.Combine(Paths.GameRootPath, fontFile));
            candidates.Add(Path.Combine(Paths.GameRootPath, "Fonts", fontFile));
            candidates.Add(Path.Combine(Paths.BepInExRootPath, fontFile));
        }

        foreach (var cand in candidates)
        {
            if (File.Exists(cand))
            {
                ttfFontPath = cand;
                LogSource.LogInfo($"Using Chinese font: {cand} (loaded on main thread at startup)");
                return;
            }
        }

        // 1.4.1 默认值迁移：老配置里的 Microsoft YaHei 走动态字体在多数机器缺字变方框，
        // 若补丁自带字体文件存在则优先用自带（用户显式选其它字体名不受影响）。
        if (string.Equals(fontFile, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.Combine(Paths.GameRootPath, "Fonts", "Alibaba-PuHuiTi-Medium.ttf");
            if (File.Exists(bundled))
            {
                ttfFontPath = bundled;
                LogSource.LogInfo($"Migrating legacy default font \"Microsoft YaHei\" to bundled font: {bundled}");
                return;
            }
        }

        // 文件未找到：若值像字体名（不含路径/扩展名），按系统字体名处理（如 Microsoft YaHei）
        if (!fontFile.Contains('/') && !fontFile.Contains('\\') && !fontFile.Contains('.'))
        {
            preferredOsFontName = fontFile;
            LogSource.LogInfo($"Font file not found; using \"{fontFile}\" as system font name (dynamic font on main thread at startup)");
            return;
        }

        LogSource.LogWarning($"Font file not found: {fontFile} (tried {candidates.Count} locations). Check cfg FontFile or put the font in the game folder.");
    }

    /// <summary>主线程（Postfix）调用：创建中文字体（只执行一次）。</summary>
    private static void EnsureMainThreadFontStep()
    {
        if (cjkTmpFontAsset != null || ttfAttempted)
            return;
        if (ttfFontPath == null && preferredOsFontName == null)
            return;

        ttfAttempted = true;
        LoadTtfFont(ttfFontPath ?? string.Empty);
    }

    /// <summary>判断路径是否是原始字体文件（而非 Unity AssetBundle）。</summary>
    private static bool IsRawFontFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        return path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".ttcf", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadTtfFont(string ttfPath)
    {
        try
        {
            var fontType = SafeType("UnityEngine.Font");
            var tmpFontAssetType = SafeType("TMPro.TMP_FontAsset");
            if (fontType == null || tmpFontAssetType == null)
            {
                LogSource.LogWarning("Font: cannot find Font/TMP_FontAsset types.");
                return;
            }

            var createFont = fontType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Internal_CreateFont"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == fontType
                                     && m.GetParameters()[1].ParameterType == typeof(string));
            var createAsset = tmpFontAssetType.GetMethods()
                .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType.FullName == "UnityEngine.Font");
            if (createFont == null || createAsset == null)
            {
                LogSource.LogWarning("Font: required Font APIs missing.");
                return;
            }

            // 优先 -1：路径不是原始字体文件 → 视为 Unity 6 TMP 字体资源包（6000v2 等）
            // AssetBundle 探测只对真正的资源包做：msyh.ttc 这类字体集合文件用 LoadFromMemory 加载会
            // 在 il2cpp_gchandle_get_target 触发 AccessViolation（catch 拦不住），整个游戏直接崩掉。
            // .ttf/.otf/.ttc 一律交给下面的 TryCreateFontAssetFromPath，那条路已验证可以加载 msyh.ttc。
            if (!IsRawFontFile(ttfPath))
            {
                if (File.Exists(ttfPath))
                {
                    var bundleFont = TryLoadFontFromBundle(ttfPath, tmpFontAssetType);
                    if (bundleFont != null)
                    {
                        cjkTmpFontAsset = bundleFont;
                        LogSource.LogInfo($"Created TMP Chinese font from asset bundle: {ttfPath}");
                        LogFontStats(bundleFont, tmpFontAssetType);
                        RegisterTmpFont(bundleFont, tmpFontAssetType, translationEnabled && CfgReplaceDefaultFont?.Value == true);
                        return;
                    }
                    LogSource.LogWarning($"Font bundle load failed: {ttfPath}; trying other sources.");
                }
            }

            // 优先 0：直接用 TMP 原生运行时 API 从 TTF 文件创建（不经过 Font 对象）
            if (File.Exists(ttfPath))
            {
                var tmpPathFont = TryCreateFontAssetFromPath(ttfPath, tmpFontAssetType);
                if (tmpPathFont != null)
                {
                    cjkTmpFontAsset = tmpPathFont;
                    LogSource.LogInfo($"Created TMP Chinese font from TTF: {ttfPath}");
                    RegisterTmpFont(tmpPathFont, tmpFontAssetType, translationEnabled && CfgReplaceDefaultFont?.Value == true);
                    return;
                }
            }

            // 优先：通过 Internal_CreateDynamicFont 创建动态字体（CreateFontAsset 需要 dynamic=True）
            foreach (var osName in GetFallbackFontNames())
            {
                var dynFont = TryCreateDynamicFont(osName);
                if (dynFont == null)
                    continue;

                var tmpFont = createAsset.Invoke(null, new object[] { dynFont });
                if (tmpFont == null)
                    tmpFont = TryCreateFontAssetFull(dynFont, tmpFontAssetType);
                if (tmpFont != null)
                {
                    cjkTmpFontAsset = tmpFont;
                    LogSource.LogInfo($"Created dynamic TMP Chinese font: {osName}");
                    RegisterTmpFont(tmpFont, tmpFontAssetType, translationEnabled && CfgReplaceDefaultFont?.Value == true);
                    return;
                }
            }

            // 尝试列表：先 TTF 文件路径，再系统 CJK 字体名
            var fallbackNames = GetFallbackFontNames();
            var attempts = new List<string> { ttfPath };
            attempts.AddRange(fallbackNames);
            var fontCtor = fontType.GetConstructor(Type.EmptyTypes);

            foreach (var source in attempts)
            {
                object? tmpFont = null;
                try
                {
                    // 用参数less 构造创建 Font，再调用 Internal_CreateFont
                    var font = fontCtor?.Invoke(null);
                    if (font == null)
                        continue;
                    createFont.Invoke(null, new object[] { font, source });

                    // 诊断：字体是否有效
                    var nameProp = fontType.GetProperty("name");
                    var dynProp = fontType.GetProperty("dynamic");
                    var fname = nameProp?.GetValue(font, null);
                    var dyn = dynProp?.GetValue(font, null);
                    LogSource.LogInfo($"Font: {source} -> Font(name={fname}, dynamic={dyn})");

                    tmpFont = createAsset.Invoke(null, new object[] { font });
                }
                catch (Exception ex)
                {
                    LogSource.LogInfo($"Font: {source} create error: {ex.Message}");
                }

                if (tmpFont != null)
                {
                    cjkTmpFontAsset = tmpFont;
                    LogSource.LogInfo($"Created TMP font from {source}.");
                    RegisterTmpFont(tmpFont, tmpFontAssetType, translationEnabled && CfgReplaceDefaultFont?.Value == true);
                    return;
                }
            }

            LogSource.LogWarning("Font: all font sources failed to create a TMP font.");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"TTF font load failed (translation unaffected): {(ex.InnerException ?? ex)}");
        }
    }

    /// <summary>cfg 配置的后备系统字体名列表（逗号/分号分隔）。</summary>
    private static string[] GetFallbackFontNames()
    {
        var v = CfgFallbackFontNames?.Value;
        var names = string.IsNullOrWhiteSpace(v)
            ? new[] { "Microsoft YaHei", "SimHei", "SimSun", "NSimSun", "Noto Sans SC" }
            : v.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // FontFile 填的是系统字体名时，优先尝试它
        if (preferredOsFontName != null && !names.Contains(preferredOsFontName, StringComparer.OrdinalIgnoreCase))
            names = new[] { preferredOsFontName }.Concat(names).ToArray();
        return names;
    }

    /// <summary>从 Unity 6 TMP 字体资源包加载 TMP_FontAsset（6000v2 等 sorrowmoil 打包的 bundle）。</summary>
    private static object? TryLoadFontFromBundle(string bundlePath, Type tmpFontAssetType)
    {
        try
        {
            var bundleType = SafeType("UnityEngine.AssetBundle");
            if (bundleType == null)
                return null;

            var loadFromFile = bundleType.GetMethods()
                .FirstOrDefault(m => m.Name == "LoadFromFile" && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            var loadFromStream = bundleType.GetMethods()
                .FirstOrDefault(m => m.Name == "LoadFromStream" && m.GetParameters().Length == 1);

            object? bundle = null;

            // 方案 1：LoadFromStream(Il2CppSystem.IO.Stream) —— 不走 span，已验证可用
            if (loadFromStream != null)
            {
                try
                {
                    var fileType = SafeType("Il2CppSystem.IO.File");
                    var openRead = fileType?.GetMethod("OpenRead", new[] { typeof(string) });
                    var stream = openRead?.Invoke(null, new object[] { bundlePath });
                    if (stream != null)
                    {
                        pinnedStream = stream; // 防 GC
                        bundle = loadFromStream.Invoke(null, new object[] { stream });
                        LogSource.LogInfo($"Font: LoadFromStream result = {(bundle == null ? "null" : "OK")}");
                    }
                }
                catch (Exception ex)
                {
                    LogSource.LogInfo($"Font: LoadFromStream failed: {(ex.InnerException ?? ex).Message}");
                }
            }

            // 方案 2：LoadFromMemory 已删除。它内部走 new Il2CppSystem.Span<byte>(Il2CppStructArray<byte>)，
            // 本游戏 interop 下会在 il2cpp_gchandle_get_target 触发 AccessViolationException——catch 拦不住，
            // 直接把游戏崩死（BepInEx\ErrorLog.log "Fatal error"）。LoadFromStream 已覆盖同一用途。

            // 方案 3：LoadFromFile(string) —— 备用（该游戏 interop 的 span 转换有缺陷，通常不可用）
            if (bundle == null && loadFromFile != null)
            {
                try
                {
                    bundle = loadFromFile.Invoke(null, new object[] { bundlePath });
                    LogSource.LogInfo($"Font: LoadFromFile result = {(bundle == null ? "null" : "OK")}");
                }
                catch (Exception ex)
                {
                    LogSource.LogInfo($"Font: LoadFromFile failed: {(ex.InnerException ?? ex).Message}");
                }
            }

            if (bundle == null)
            {
                LogSource.LogInfo("Font: all AssetBundle load methods failed.");
                return null;
            }

            object? result = null;
            // 方案 A：泛型 LoadAllAssetsAsync<T>() → request.allAssets（不走 span，之前验证可用）
            var loadAllAsync = bundle.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "LoadAllAssetsAsync" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (loadAllAsync != null)
            {
                try
                {
                    var request = loadAllAsync.MakeGenericMethod(tmpFontAssetType).Invoke(bundle, null);
                    pinnedRequest = request; // 防 GC
                    var allAssets = request?.GetType().GetProperty("allAssets")?.GetValue(request, null);
                    result = GetFirstArrayItem(allAssets, tmpFontAssetType);
                }
                catch (Exception ex)
                {
                    LogSource.LogInfo($"Font: LoadAllAssetsAsync failed: {(ex.InnerException ?? ex).Message}");
                }
            }

            // 方案 B：泛型 LoadAll<T>()
            if (result == null)
            {
                var loadAll = bundle.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "LoadAll" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (loadAll != null)
                {
                    try
                    {
                        var arr = loadAll.MakeGenericMethod(tmpFontAssetType).Invoke(bundle, null);
                        result = GetFirstArrayItem(arr, tmpFontAssetType);
                    }
                    catch (Exception ex)
                    {
                        LogSource.LogInfo($"Font: LoadAll<T> failed: {(ex.InnerException ?? ex).Message}");
                    }
                }
            }

            // 方案 C：泛型 LoadAllAssets<T>() 同步
            if (result == null)
            {
                var loadAll = bundle.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "LoadAllAssets" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (loadAll != null)
                {
                    try
                    {
                        var arr = loadAll.MakeGenericMethod(tmpFontAssetType).Invoke(bundle, null);
                        result = GetFirstArrayItem(arr, tmpFontAssetType);
                    }
                    catch (Exception ex)
                    {
                        LogSource.LogInfo($"Font: LoadAllAssets<T> failed: {(ex.InnerException ?? ex).Message}");
                    }
                }
            }

            if (result == null)
                LogSource.LogInfo("Font: no TMP_FontAsset found in bundle.");
            return result;
        }
        catch (Exception ex)
        {
            LogSource.LogInfo($"Font: bundle load failed: {(ex.InnerException ?? ex).Message}");
            return null;
        }
    }

    /// <summary>取数组第一个元素，并按真实类型重包装（interop 数组元素常按 UnityEngine.Object 声明类型返回）。</summary>
    private static object? GetFirstArrayItem(object? arr, Type itemType)
    {
        if (arr == null)
            return null;
        try
        {
            var arrType = arr.GetType();
            var lenProp = arrType.GetProperty("Length") ?? arrType.GetProperty("Count");
            var lenVal = lenProp?.GetValue(arr, null);
            if (lenVal == null || !int.TryParse(lenVal.ToString(), out var len) || len == 0)
                return null;

            object? item = null;
            var idxProp = arrType.GetProperty("Item");
            if (idxProp != null)
                item = idxProp.GetValue(arr, new object[] { 0 });
            else if (arr is System.Collections.IEnumerable en)
            {
                foreach (var e in en) { item = e; break; }
            }
            if (item == null)
                return null;

            return WrapAs(itemType, item);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>System.Type → Il2CppSystem.Type（通过反射调用 Il2CppType.From，避免引入 Il2Cppmscorlib 编译引用）。</summary>
    private static object ToIl2CppType(Type managedType)
    {
        var from = typeof(Il2CppInterop.Runtime.Il2CppType).GetMethod("From", new[] { typeof(Type) });
        if (from == null)
            throw new InvalidOperationException("Il2CppType.From 不存在");
        return from.Invoke(null, new object[] { managedType })!;
    }

    /// <summary>把按声明类型（如 UnityEngine.Object）返回的包装，重新包装成真实类型（如 TMP_FontAsset）。</summary>
    private static object WrapAs(Type targetType, object raw)
    {
        if (raw.GetType() == targetType)
            return raw;
        try
        {
            var ptrProp = raw.GetType().GetProperty("Pointer");
            var ptrVal = ptrProp?.GetValue(raw, null);
            if (ptrVal is not IntPtr p || p == IntPtr.Zero)
                return raw;
            var pool = typeof(Il2CppInterop.Runtime.Runtime.Il2CppObjectPool);
            var get = pool.GetMethods().FirstOrDefault(mi =>
                mi.Name == "Get" && mi.IsGenericMethodDefinition
                && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(IntPtr));
            if (get == null)
                return raw;
            return get.MakeGenericMethod(targetType).Invoke(null, new object[] { p })!;
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>尝试完整参数的 CreateFontAsset(Font, size, padding, mode, atlasW, atlasH, popMode, clear)。</summary>
    private static object? TryCreateFontAssetFull(object font, Type tmpFontAssetType)
    {
        try
        {
            var fullCreate = tmpFontAssetType.GetMethods()
                .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 8);
            if (fullCreate == null)
                return null;

            var glyphModeType = SafeType("UnityEngine.TextCore.LowLevel.GlyphRenderMode");
            var atlasModeType = SafeType("TMPro.AtlasPopulationMode");
            var sdfaa = glyphModeType == null ? null : Enum.Parse(glyphModeType, "SDFAA");
            var dynMode = atlasModeType == null ? null : Enum.Parse(atlasModeType, "Dynamic");

            var result = fullCreate.Invoke(null, new object[] { font, 16, 4, sdfaa!, 1024, 1024, dynMode!, false });
            LogSource.LogInfo($"Font: full CreateFontAsset -> {(result == null ? "null" : result.GetType().Name)}");
            return result;
        }
        catch (Exception ex)
        {
            LogSource.LogInfo($"Font: full CreateFontAsset failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>用 TMP 原生运行时 API CreateFontAsset(string filePath, ...) 直接从 TTF 文件创建（不经过 Font 对象）。</summary>
    private static object? TryCreateFontAssetFromPath(string ttfPath, Type tmpFontAssetType)
    {
        try
        {
            var glyphModeType = SafeType("UnityEngine.TextCore.LowLevel.GlyphRenderMode");
            var atlasModeType = SafeType("TMPro.AtlasPopulationMode");
            var sdfaa = glyphModeType == null ? null : Enum.Parse(glyphModeType, "SDFAA");
            var dynMode = atlasModeType == null ? null : Enum.Parse(atlasModeType, "Dynamic");

            // 9 参数(私有): 追加 atlasPopulationMode / enableMultiAtlasSupport —— 优先，显式 Dynamic + 1024 图集 + 多图集扩容
            var create9 = tmpFontAssetType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 9
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (create9 != null)
            {
                var r = create9.Invoke(null, new object[] { ttfPath, 0, 16, 4, sdfaa!, 1024, 1024, dynMode!, true });
                if (r != null)
                {
                    TestGlyphPopulation(r);
                    return r;
                }
            }

            // 7 参数: CreateFontAsset(string filePath, int faceIndex, int samplingPointSize, int atlasPadding, GlyphRenderMode renderMode, int atlasWidth, int atlasHeight)
            var create7 = tmpFontAssetType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 7
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (create7 != null)
            {
                var r = create7.Invoke(null, new object[] { ttfPath, 0, 16, 4, sdfaa!, 256, 256 });
                if (r != null)
                {
                    TestGlyphPopulation(r);
                    return r;
                }
            }

            // 3 参数: CreateFontAsset(string familyName, string styleName, int pointSize) — 按系统字体名
            var create3 = tmpFontAssetType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateFontAsset" && m.GetParameters().Length == 3
                                     && m.GetParameters()[0].ParameterType == typeof(string)
                                     && m.GetParameters()[1].ParameterType == typeof(string));
            if (create3 != null)
            {
                foreach (var osName in new[] { "Microsoft YaHei", "SimHei", "SimSun", "NSimSun", "Noto Sans SC" })
                {
                    var r = create3.Invoke(null, new object[] { osName, "", 16 });
                    LogSource.LogInfo($"Font: TMP system font create({osName}) -> {(r == null ? "null" : r.GetType().Name)}");
                    if (r != null) return r;
                }
            }
        }
        catch (Exception ex)
        {
            LogSource.LogInfo($"Font: TMP path create failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>直接调用 TryAddCharacters 强制向动态字体填充字符，验证图集是否真实可扩展。</summary>
    private static void TestGlyphPopulation(object asset)
    {
        try
        {
            var t = asset.GetType();
            var tryAdd = t.GetMethods()
                .FirstOrDefault(m => m.Name == "TryAddCharacters" && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(string)
                                     && m.GetParameters()[1].ParameterType == typeof(bool));
            if (tryAdd == null)
            {
                LogSource.LogInfo("Font: TryAddCharacters(string,bool) not found.");
                return;
            }

            int charsBefore = GetCharCount(asset);
            var texBefore = GetAtlasSize(asset);
            var result = tryAdd.Invoke(asset, new object[] { "中文字体测试，漢字。" , false });
            int charsAfter = GetCharCount(asset);
            var texAfter = GetAtlasSize(asset);
            LogSource.LogInfo($"Font: glyph population check TryAddCharacters={result} chars {charsBefore}->{charsAfter} atlas {texBefore}->{texAfter}");
        }
        catch (Exception ex)
        {
            LogSource.LogInfo($"Font: population check failed: {(ex.InnerException ?? ex).Message}");
        }
    }

    /// <summary>
    /// 把全部词条译文用到的字符一次性烘进中文字体动态图集。
    /// 回退链按 characterTable 解析字形，纯动态按需生成对部件名等生僻字来不及（图集满/未注册）→ 方框。
    /// </summary>
    private static void PopulateFontFromTranslations()
    {
        var asset = cjkTmpFontAsset;
        if (asset == null)
            return;
        try
        {
            var chars = new HashSet<char>();
            foreach (var v in Translations.Values)
                foreach (var ch in v)
                    if (ch > 0x7F && !char.IsWhiteSpace(ch))
                        chars.Add(ch);
            if (chars.Count == 0)
                return;

            var tryAdd = asset.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "TryAddCharacters" && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (tryAdd == null)
            {
                LogSource.LogWarning("Font: TryAddCharacters(string,…) not found; skip bulk population.");
                return;
            }

            var arr = chars.ToArray();
            const int chunk = 256;
            int ok = 0, bad = 0;
            for (int i = 0; i < arr.Length; i += chunk)
            {
                var s = new string(arr, i, Math.Min(chunk, arr.Length - i));
                try
                {
                    var res = tryAdd.Invoke(asset, new object[] { s, false });
                    if (res is bool b && b) ok += s.Length; else bad += s.Length;
                }
                catch
                {
                    bad += s.Length;
                }
            }
            LogSource.LogInfo($"Font: bulk glyph population {arr.Length} unique chars (ok {ok}, failed {bad}); characterTable={GetCharCount(asset)} atlas:{GetAtlasSize(asset)}");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"Font: bulk glyph population failed (non-fatal): {(ex.InnerException ?? ex).Message}");
        }
    }

    private static int GetCharCount(object asset)
    {
        try
        {
            var ct = asset.GetType().GetProperty("characterTable")?.GetValue(asset, null);
            if (ct == null) return -1;
            var cv = ct.GetType().GetProperty("Count")?.GetValue(ct, null);
            return cv != null && int.TryParse(cv.ToString(), out var c) ? c : -1;
        }
        catch { return -1; }
    }

    private static string GetAtlasSize(object asset)
    {
        try
        {
            var t = asset.GetType();
            var arr = t.GetProperty("atlasTextures")?.GetValue(asset, null);
            if (arr != null)
            {
                var lenProp = arr.GetType().GetProperty("Length") ?? arr.GetType().GetProperty("Count");
                var len = lenProp?.GetValue(arr, null);
                var ien = arr as System.Collections.IEnumerable;
                string sizes = "";
                if (ien != null)
                {
                    foreach (var item in ien)
                    {
                        var w = item.GetType().GetProperty("width")?.GetValue(item, null);
                        var h = item.GetType().GetProperty("height")?.GetValue(item, null);
                        sizes += $" {w}x{h}";
                    }
                }
                return $"len={len}{sizes}";
            }
            var tex = t.GetProperty("atlasTexture")?.GetValue(asset, null);
            if (tex != null)
            {
                var w = tex.GetType().GetProperty("width")?.GetValue(tex, null);
                var h = tex.GetType().GetProperty("height")?.GetValue(tex, null);
                return $"{w}x{h}";
            }
            return "null";
        }
        catch { return "?"; }
    }

    /// <summary>通过 Font.Internal_CreateDynamicFont(font, names, size) 创建动态字体。</summary>
    private static object? TryCreateDynamicFont(string fontName)
    {
        try
        {
            var fontType = SafeType("UnityEngine.Font");
            var fontCtor = fontType?.GetConstructor(Type.EmptyTypes);
            var font = fontCtor?.Invoke(null);
            if (font == null)
                return null;

            var internalCreate = fontType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Internal_CreateDynamicFont" && m.GetParameters().Length == 3);
            if (internalCreate == null)
            {
                LogSource.LogInfo("Font: Internal_CreateDynamicFont not found.");
                return null;
            }

            var names = new Il2CppStringArray(new[] { fontName });
            internalCreate.Invoke(null, new object[] { font, names, 16 });

            var dynProp = fontType!.GetProperty("dynamic");
            var matProp = fontType.GetProperty("material");
            var nameProp2 = fontType.GetProperty("name");
            var dyn = dynProp?.GetValue(font, null);
            var mat = matProp?.GetValue(font, null);
            var nm = nameProp2?.GetValue(font, null);
            LogSource.LogInfo($"Font: Internal_CreateDynamicFont({fontName}) -> dynamic={dyn} material={(mat == null ? "null" : mat.GetType().Name)} name={nm}");
            return font;
        }
        catch (Exception ex)
        {
            LogSource.LogInfo($"Font: dynamic font {fontName} failed: {ex.Message}");
            return null;
        }
    }

    private static void RegisterTmpFont(object tmpFont, Type tmpFontAssetType, bool replaceDefault = true)
    {
        try
        {
            var settingsType = SafeType("TMPro.TMP_Settings");
            if (settingsType == null)
                return;

            // 先取游戏原默认字体（通常为 LiberationSans），把我们的字体注入其回退链
            object? originalDefault = null;
            var defaultGetter = AccessTools.PropertyGetter(settingsType, "defaultFontAsset");
            try { originalDefault = defaultGetter?.Invoke(null, null); } catch { }

            // 仅翻译开启时替换默认字体；翻译关闭时保留原默认字体（避免影响原文渲染 → 乱码/方框）
            if (replaceDefault)
            {
                var defaultSetter = AccessTools.PropertySetter(settingsType, "defaultFontAsset");
                defaultSetter?.Invoke(null, new object[] { tmpFont });
            }

            // TMP_Settings.fallbackFontAssets.Add(tmpFont)
            var getter = AccessTools.PropertyGetter(settingsType, "fallbackFontAssets");
            var list = getter?.Invoke(null, null);
            if (list != null)
            {
                var add = list.GetType().GetMethod("Add", new[] { tmpFontAssetType });
                add?.Invoke(list, new object[] { tmpFont });
            }

            // 注入回退链：原默认字体（通常为 LiberationSans），使已有文本组件的中文缺字回退到我们的字体
            int injected = 0;
            if (originalDefault != null && !ReferenceEquals(originalDefault, tmpFont))
                injected = AddToFallbackTable(originalDefault, tmpFont);
            LogSource.LogInfo(replaceDefault
                ? $"Registered TMP default/fallback Chinese font (injected fallback chain: {injected} assets)."
                : $"Registered TMP fallback Chinese font (default font untouched, original rendering unaffected; injected fallback chain: {injected} assets).");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"TMP font registration failed: {(ex.InnerException ?? ex)}");
        }
    }

    private static void LogFontStats(object fontAsset, Type tmpFontAssetType)
    {
        try
        {
            var t = fontAsset.GetType();
            // 字符数
            var ct = t.GetProperty("characterTable")?.GetValue(fontAsset, null);
            int chars = -1;
            if (ct != null)
            {
                var cv = ct.GetType().GetProperty("Count")?.GetValue(ct, null);
                if (cv != null && int.TryParse(cv.ToString(), out var c)) chars = c;
            }
            // 填充模式
            var mode = t.GetProperty("atlasPopulationMode")?.GetValue(fontAsset, null);
            // 图集
            var atlas = t.GetProperty("atlasTextures")?.GetValue(fontAsset, null);
            string atlasInfo = "?";
            if (atlas != null)
            {
                var lenProp = atlas.GetType().GetProperty("Length") ?? atlas.GetType().GetProperty("Count");
                var len = lenProp?.GetValue(atlas, null);
                var tex0 = atlas.GetType().GetProperty("Item")?.GetValue(atlas, new object[] { 0 });
                if (tex0 != null)
                {
                    var w = tex0.GetType().GetProperty("width")?.GetValue(tex0, null);
                    var h = tex0.GetType().GetProperty("height")?.GetValue(tex0, null);
                    atlasInfo = $"{len} x {w}x{h}";
                }
            }
            LogSource.LogInfo($"Font stats: chars={chars} mode={mode} atlas={atlasInfo} (chars高=预烘焙大图集, chars低≈需运行时补字形)");
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"Font stats read failed: {ex.Message}");
        }
    }

    /// <summary>把 tmpFont 追加到 fontAsset 的 fallbackFontAssetTable（兼容 TMP3/4 的属性/字段命名）。</summary>
    private static int AddToFallbackTable(object fontAsset, object tmpFont)
    {
        try
        {
            var t = fontAsset.GetType();
            var prop = t.GetProperties().FirstOrDefault(p =>
                p.Name.IndexOf("FallbackFontAssetTable", StringComparison.OrdinalIgnoreCase) >= 0);
            var field = t.GetFields().FirstOrDefault(f =>
                f.Name.IndexOf("FallbackFontAssetTable", StringComparison.OrdinalIgnoreCase) >= 0);
            object? table = prop != null ? prop.GetValue(fontAsset, null) : field?.GetValue(fontAsset);
            if (table == null)
                return 0;

            var contains = table.GetType().GetMethod("Contains", new[] { tmpFont.GetType() });
            if (contains != null)
            {
                var containsResult = contains.Invoke(table, new object[] { tmpFont });
                if (containsResult is bool containsBool && containsBool)
                    return 0;
            }

            var add = table.GetType().GetMethod("Add", new[] { tmpFont.GetType() });
            add?.Invoke(table, new object[] { tmpFont });
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    // ---------------- 翻译文件 ----------------

    // 内嵌词条资源（deflate+XOR），打包前由 tools/encrypt_entries.mjs 生成并随 csproj 嵌入。
    // 外部 Translations\*.txt 在其后加载并覆盖同名词条，自定义词条仍然直接放 txt 即可生效。
    private const string EmbeddedResourceName = "SprocketTranslation.entries.bin";
    private static readonly byte[] EntryKey =
    {
        0x5A, 0xC3, 0x17, 0xE8, 0x6B, 0x2F, 0x94, 0xD1,
        0x08, 0xBB, 0x4E, 0xA2, 0x73, 0x5C, 0xFE, 0x39
    };

    private static int LoadTranslations()
    {
        Translations.Clear();
        int count = LoadEmbeddedTranslations();
        count += LoadExternalTranslationFiles();
        return count;
    }

    private static int LoadEmbeddedTranslations()
    {
        int count = 0;
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (resource == null)
            {
                LogSource.LogWarning("Embedded translations resource missing; only external .txt entries will load.");
                return 0;
            }

            using var ms = new MemoryStream();
            resource.CopyTo(ms);
            var data = ms.ToArray();
            for (int i = 0; i < data.Length; i++)
                data[i] ^= EntryKey[i % EntryKey.Length];

            using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (ApplyEntryLine(line))
                    count++;
            }
            LogSource.LogInfo($"Embedded translations loaded: {count} entries.");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"Failed to load embedded translations: {ex.Message}");
        }
        return count;
    }

    private static int LoadExternalTranslationFiles()
    {
        int count = 0;

        var dirs = new[]
        {
            Path.Combine(Paths.GameRootPath, "Translations"),
            Path.Combine(Paths.BepInExRootPath, "Translations"),
        };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.AllDirectories))
            {
                count += LoadTranslationFile(file);
            }
        }

        return count;
    }

    /// <summary>找到“原文=译文”的分隔符：第一个不在富文本标签内的 '='（避免 <color=#...> 等标签内的 '=' 截断 key）。</summary>
    private static int FindKeyValueSeparator(string line)
    {
        bool inTag = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '<')
                inTag = true;
            else if (c == '>' && inTag)
                inTag = false;
            else if (c == '=' && !inTag)
            {
                // "\=" 为转义的等号（XUnity.AutoTranslator 约定），不作为分隔符
                if (i > 0 && line[i - 1] == '\\')
                    continue;
                return i;
            }
        }
        return -1;
    }

    /// <summary>解析并登记一行 “原文=译文”。返回 true 表示成功加载一条。</summary>
    private static bool ApplyEntryLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            return false;

        int eq = FindKeyValueSeparator(line);
        if (eq <= 0)
            return false;

        var key = NormalizeNewlines(Unescape(line.Substring(0, eq)));
        var value = Unescape(line.Substring(eq + 1));

        if (key.Length == 0 || value.Length == 0)
            return false;

        Translations[key] = value;
        return true;
    }

    private static int LoadTranslationFile(string path)
    {
        int count = 0;
        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                if (ApplyEntryLine(rawLine))
                    count++;
            }
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"Failed to read translation file {path}: {ex.Message}");
        }

        return count;
    }

    // ---------------- 文本提取 ----------------

    private static void InitExtractionFile()
    {
        try
        {
            var dir = Path.Combine(Paths.GameRootPath, "Translations");
            Directory.CreateDirectory(dir);
            extractionFilePath = Path.Combine(dir, "_ExtractedText.txt");
            ExtractedKeys.Clear();
            if (File.Exists(extractionFilePath))
            {
                foreach (var line in File.ReadAllLines(extractionFilePath))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal))
                        continue;
                    int eq = FindKeyValueSeparator(t);
                    if (eq > 0)
                        ExtractedKeys.Add(NormalizeNewlines(Unescape(t.Substring(0, eq))));
                }
            }
            else
            {
                File.WriteAllText(extractionFilePath,
                    "# 由 SprocketTranslation（汉化：七夜已逝 / Sevenight）自动提取的未翻译原文，每行一条；“=”后填上译文即可生效。数字已替换为 {{A}} 占位符（如 “燃料：{{A}}”）。\n",
                    new UTF8Encoding(false));
            }
            LogSource.LogInfo($"Text extraction ready: {extractionFilePath} ({ExtractedKeys.Count} entries)");
        }
        catch (Exception ex)
        {
            LogSource.LogWarning($"Text extraction init failed: {ex.Message}");
        }
    }

    private static void RecordUntranslated(string original)
    {
        original = NormalizeNewlines(original);
        // 先把数字归一化为 {{A}} 模板，提取文件里直接是可用的翻译模板
        var template = NormalizeSmartNumbers(original, out _);
        if (string.IsNullOrEmpty(extractionFilePath) || string.IsNullOrWhiteSpace(template))
            return;
        // 已翻译的文本不再提取：原文已在字典中（精确），或模板已在字典中（{{A}} 模板 / 旧版纯数字模板），
        // 或通配符词条已覆盖，或已在提取文件里
        if (Translations.ContainsKey(original) || Translations.ContainsKey(template) || ExtractedKeys.Contains(template))
            return;
        var legacyTemplate = NormalizeLegacyNumbers(original, out _);
        if (legacyTemplate != template && Translations.ContainsKey(legacyTemplate))
            return;
        if (TryWildcardMatch(original) != null)
            return;
        if (!LooksTranslatable(template))
            return;

        ExtractedKeys.Add(template);
        try
        {
            // 原文自身可能含 '='（如 "Largest step = 300rpm"），必须转义，否则回读时键值切错位
            var escaped = template.Replace("=", "\\=").Replace("\n", "\\n").Replace("\r", "\\r");
            File.AppendAllText(extractionFilePath, escaped + "=\n", new UTF8Encoding(false));
            LogSource.LogDebug($"Extracted: {escaped}");
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"Extraction write failed: {ex.Message}");
        }
    }

    /// <summary>过滤噪音：已含中文、去掉 {{A}} 占位符后不含任何字母（纯数字/版本号）的行不提取。</summary>
    private static bool LooksTranslatable(string s)
    {
        if (s.Length == 0)
            return false;
        if (s.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            return false;
        var noPlaceholders = PlaceholderRegex.Replace(s, "");
        if (!noPlaceholders.Any(char.IsLetter))
            return false;
        return true;
    }

    // ---------------- Hook ----------------

    private int PatchTextSetters()
    {
        int hooked = 0;

        hooked += TryPatchSetter("TMPro.TMP_Text", "text");
        hooked += TryPatchMethod("TMPro.TMP_Text", "SetText", new[] { typeof(string) });
        hooked += TryPatchSetter("UnityEngine.UI.Text", "text");
        hooked += TryPatchSetter("UnityEngine.TextMesh", "text");
        hooked += TryPatchSetter("UnityEngine.UIElements.TextElement", "text");

        // TMP 格式重载 SetText(string, float/bool...) —— TMP 内部解析 {0} 后写文本，不经 set_text，
        // 游戏里大量动态文本（如部件统计、Faction）走这里，不钩则无法翻译
        hooked += PatchFormattedSetText("TMPro.TMP_Text");

        return hooked;
    }

    /// <summary>钩住 TMP_Text 的 SetText(string, params) 格式重载（2..10 参数、首参 string、其余 float/bool）。</summary>
    private int PatchFormattedSetText(string typeName)
    {
        int hooked = 0;
        if (CfgFormatHooks?.Value == false)
        {
            Log.LogInfo("Format hooks disabled by cfg (FormatHooksEnabled=false).");
            return 0;
        }
        var type = SafeType(typeName);
        if (type == null)
            return 0;

        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.Name != "SetText")
                continue;
            var ps = m.GetParameters();
            if (ps.Length < 2 || ps.Length > 10)
                continue;
            if (ps[0].ParameterType != typeof(string))
                continue;
            // 其余参数 float/bool
            bool formatArgs = true;
            for (int i = 1; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (pt != typeof(float) && pt != typeof(bool))
                {
                    formatArgs = false;
                    break;
                }
            }
            if (!formatArgs)
                continue;
            if (!PatchedMethods.Add(m))
                continue;
            try
            {
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TranslateFormattedPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                hooked++;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Hook SetText format({ps.Length}) failed: {ex.Message}");
            }
        }
        if (hooked > 0)
            Log.LogInfo($"Hooked {hooked} TMP SetText format overloads.");
        return hooked;
    }

    private int TryPatchSetter(string typeName, string propertyName)
    {
        var type = SafeType(typeName);
        if (type == null)
        {
            Log.LogInfo($"Type not found: {typeName}, skipped.");
            return 0;
        }

        var setter = AccessTools.PropertySetter(type, propertyName);
        if (setter == null)
        {
            Log.LogInfo($"Setter not found: {typeName}.{propertyName}, skipped.");
            return 0;
        }

        return ApplyPatch(setter);
    }

    private int TryPatchMethod(string typeName, string methodName, Type[] parameters)
    {
        var type = SafeType(typeName);
        if (type == null)
        {
            Log.LogInfo($"Type not found: {typeName}, skipped.");
            return 0;
        }

        var method = AccessTools.Method(type, methodName, parameters);
        if (method == null)
        {
            Log.LogInfo($"Method not found: {typeName}.{methodName}(...), skipped.");
            return 0;
        }

        return ApplyPatch(method);
    }

    private int ApplyPatch(MethodBase target)
    {
        if (!PatchedMethods.Add(target))
            return 0;

        try
        {
            harmony.Patch(
                target,
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(TranslatePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            Log.LogInfo($"Hooked {target.DeclaringType?.FullName}.{target.Name}.");
            return 1;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Hook {target.DeclaringType?.FullName}.{target.Name} failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Harmony Postfix：文本写入后再用译文覆盖。__0 = 第一个参数（文本）。</summary>
    private static void TranslatePostfix(object __instance, string __0)
    {
        try
        {
            TranslatePostfixInner(__instance, __0);
        }
        catch (Exception ex)
        {
            // 任何异常都不能让游戏闪退：记录并继续显示原文
            LogSource.LogDebug($"Translation processing error: {ex.Message}");
        }
    }

    private static void TranslatePostfixInner(object __instance, string __0)
    {
        // 主线程步骤：创建中文字体（不受翻译开关影响）
        EnsureMainThreadFontStep();

        if (applyingTranslation || __0 == null)
            return;

        // 提取模式：记录未翻译原文（仅翻译开启时）
        if (translationEnabled && extractionMode)
            RecordUntranslated(__0);

        // 翻译关闭：保留原文（但中文字体已注册，缺字回退仍生效）
        if (!translationEnabled)
            return;

        long t0 = Environment.TickCount64;
        string? replacement = LookupTranslation(NormalizeNewlines(__0));
        if (replacement == null)
        {
            AccumulateHookCost(Environment.TickCount64 - t0);
            return;
        }

        if (!TextWriteCache.TryGetValue(__instance.GetType(), out var write))
        {
            write = CompileTextWriter(__instance.GetType());
            TextWriteCache[__instance.GetType()] = write!;
        }

        if (write == null)
            return;

        applyingTranslation = true;
        try
        {
            write(__instance, replacement);
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"Failed to override text: {ex.Message}");
        }
        finally
        {
            applyingTranslation = false;
        }
        AccumulateHookCost(Environment.TickCount64 - t0);
    }

    /// <summary>Harmony Postfix：SetText(format, args) 等格式重载（TMP 内部解析 {0} 后再写文本，
    /// 不经 set_text，需在此读取最终文本并翻译）。__0 = 格式字符串（如 "Weight: {0}"）。</summary>
    private static void TranslateFormattedPostfix(object __instance, string __0)
    {
        try
        {
            if (applyingTranslation)
                return;
            EnsureMainThreadFontStep();
            if (!translationEnabled)
                return;

            // 负缓存：某格式串首次归一化后模板不在字典 → 该格式的所有变体都不会命中（字典是静态的），
            // 后续直接跳过，避免零件/蓝图界面大量格式文本重复跑正则。
            if (NoMatchFormats.Contains(__0))
                return;

            var type = __instance.GetType();
            if (!TextReadCache.TryGetValue(type, out var read))
            {
                read = CompileTextReader(type);
                TextReadCache[type] = read!;
            }
            if (read == null)
                return;
            var current = read(__instance);
            if (string.IsNullOrEmpty(current))
                return;
            current = NormalizeNewlines(current);

            // 提取模式：记录格式化后的未翻译原文（Faction/部件统计等走 SetText 格式重载的文本）
            if (translationEnabled && extractionMode)
                RecordUntranslated(current);

            long t0 = Environment.TickCount64;
            string? replacement = null;
            // 精确匹配总是尝试
            if (Translations.TryGetValue(current, out var exact))
                replacement = exact;
            // 负缓存：某格式串首次模板不在字典 → 同格式变体都不会命中模板，跳过（精确已查）
            else if (!NoMatchFormats.Contains(__0))
            {
                replacement = LookupTemplateWildcard(current);
                if (replacement == null)
                    NoMatchFormats.Add(__0);
            }

            if (replacement == null || replacement == current)
            {
                AccumulateHookCost(Environment.TickCount64 - t0);
                return;
            }

            if (!TextWriteCache.TryGetValue(type, out var write))
            {
                write = CompileTextWriter(type);
                TextWriteCache[type] = write!;
            }
            if (write == null)
                return;

            applyingTranslation = true;
            try { write(__instance, replacement); }
            finally { applyingTranslation = false; }
            AccumulateHookCost(Environment.TickCount64 - t0);
        }
        catch (Exception ex)
        {
            LogSource.LogDebug($"Formatted override error: {ex.Message}");
        }
    }

    /// <summary>为某运行时类型编译 text 属性写入委托（替代每次 MethodInfo.Invoke 反射）。失败返回 null（如无 text setter）。</summary>
    private static Action<object, string>? CompileTextWriter(Type type)
    {
        try
        {
            // 先找 public（含继承，子类实例适用）；再退回仅当前类型声明的非 public
            var prop = type.GetProperty("text")
                       ?? type.GetProperty("text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var setMethod = prop?.GetSetMethod(true);
            if (setMethod == null)
                return null;
            var inst = Expression.Parameter(typeof(object), "inst");
            var val = Expression.Parameter(typeof(string), "val");
            var call = Expression.Call(Expression.Convert(inst, type), setMethod, val);
            return Expression.Lambda<Action<object, string>>(call, inst, val).Compile();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>为某运行时类型编译 text 属性读取委托（替代每次 PropertyInfo.GetValue 反射）。失败返回 null。</summary>
    private static Func<object, string>? CompileTextReader(Type type)
    {
        try
        {
            // 先找 public（含继承，子类实例适用）；再退回仅当前类型声明的非 public
            var prop = type.GetProperty("text")
                       ?? type.GetProperty("text", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var getMethod = prop?.GetGetMethod(true);
            if (getMethod == null)
                return null;
            var inst = Expression.Parameter(typeof(object), "inst");
            var call = Expression.Call(Expression.Convert(inst, type), getMethod);
            return Expression.Lambda<Func<object, string>>(call, inst).Compile();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>共享查找：精确 → {{A}} 模板 → 通配符。返回译文或 null。</summary>
    private static string? LookupTranslation(string text)
    {
        if (Translations.TryGetValue(text, out var exact))
            return exact;
        return LookupTemplateWildcard(text);
    }

    /// <summary>模板（{{A}}/旧版/通配符）查找（不含精确）。普通纯文本跳过正则。</summary>
    private static string? LookupTemplateWildcard(string text)
    {
        // 数字/富文本标签模板
        if (ContainsDigitOrTag(text))
        {
            var template = NormalizeSmartNumbers(text, out var segs);
            if (template != text && Translations.TryGetValue(template, out var templated))
                return SubstitutePlaceholders(templated, segs);

            // 旧版兼容：纯数字模板（标签内数字也替换）
            var legacy = NormalizeLegacyNumbers(text, out var segsLegacy);
            if (legacy != text && Translations.TryGetValue(legacy, out var legacyT))
                return SubstitutePlaceholders(legacyT, segsLegacy);
        }

        // 通配符匹配（无通配词条时跳过）
        if (WildcardKeys.Count > 0)
            return TryWildcardMatch(text);

        return null;
    }

    // ---------------- 智能字符串（数字 → {{A}} 占位符，兼容 XUnity.AutoTranslator 风格） ----------------

    /// <summary>数字或富文本标签：优先匹配 <color>/<b>/<size=20> 等标签（原样保留，标签内的数字不替换），
    /// 再匹配标签外的纯数字（整数/千分位/小数/负数）。按出现顺序替换为 {{A}}、{{B}}…</summary>
    private static readonly Regex RichTextOrNumberRegex = new Regex(@"<[^>]*>|-?\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    private static readonly Regex PlaceholderRegex = new Regex(@"\{\{[A-Z]+\}\}", RegexOptions.Compiled);

    /// <summary>旧版兼容：不保护富文本标签的纯数字正则（用于匹配旧版本提取的模板，如颜色标签内的数字也被替换）。</summary>
    private static readonly Regex LegacyNumberRegex = new Regex(@"-?\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>文本是否含数字或富文本标签（决定是否需要走 {{A}} 模板匹配）。普通纯文本直接跳过正则，提升性能。</summary>
    private static bool ContainsDigitOrTag(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || (c >= '0' && c <= '9'))
                return true;
        }
        return false;
    }

    /// <summary>累计翻译钩子耗时；超过阈值时日志提示（诊断零件/蓝图界面卡顿是否由翻译钩子引起）。</summary>
    private static void AccumulateHookCost(long costMs)
    {
        hookCostCalls++;
        hookCostMs += costMs;
        // 每 3000 次或累计超 25ms 且未提示过，输出一次
        if (!hookCostLogged && (hookCostCalls >= 3000 || hookCostMs >= 25))
        {
            hookCostLogged = true;
            LogSource.LogInfo($"[perf] Translation hooks: {hookCostCalls} calls, ~{hookCostMs}ms total (neg-cache formats: {NoMatchFormats.Count}). If this is high during UI open, hooks are the cost.");
        }
    }

    /// <summary>把文本中的数字归一化为 {{A}}、{{B}}…，同时收集各数字原值；富文本标签原样保留。</summary>
    private static string NormalizeSmartNumbers(string text, out List<string> values)
    {
        values = new List<string>();
        var vals = values; // out 参数不能在 lambda 中使用，先拷贝到局部
        int counter = 0;
        return RichTextOrNumberRegex.Replace(text, m =>
        {
            if (m.Value[0] == '<')
                return m.Value; // 富文本标签（含标签内的数字如 <size=20>）原样保留
            vals.Add(m.Value);
            return "{{" + ExcelColumn(counter++) + "}}";
        });
    }

    /// <summary>旧版归一化：所有数字（含标签内的）都替换为占位符。用于兼容旧版本提取的翻译模板。</summary>
    private static string NormalizeLegacyNumbers(string text, out List<string> values)
    {
        values = new List<string>();
        var vals = values;
        int counter = 0;
        return LegacyNumberRegex.Replace(text, m =>
        {
            vals.Add(m.Value);
            return "{{" + ExcelColumn(counter++) + "}}";
        });
    }

    /// <summary>把译文模板中的 {{A}}、{{B}}… 依次替换回实际数字。</summary>
    private static string SubstitutePlaceholders(string translated, List<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            translated = translated.Replace("{{" + ExcelColumn(i) + "}}", values[i]);
        }
        return translated;
    }

    /// <summary>Excel 式列名：0→A、25→Z、26→AA…</summary>
    private static string ExcelColumn(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            index--;
            sb.Insert(0, (char)('A' + index % 26));
            index /= 26;
        }
        return sb.ToString();
    }

    // ---------------- 通配符 *（处理“名字/任意内容在中间”的文本，如 *'s blueprints） ----------------

    /// <summary>把翻译字典里含 * 的 key 编译成正则缓存。* 匹配任意字符（多个 * 按顺序对应）。</summary>
    private static void BuildWildcardCache()
    {
        WildcardKeys.Clear();
        foreach (var kv in Translations)
        {
            if (kv.Key.IndexOf('*') < 0)
                continue;
            var rx = BuildWildcardRegex(kv.Key);
            if (rx != null)
                WildcardKeys.Add(new KeyValuePair<string, Regex>(kv.Value, rx));
        }
        wildcardReady = true;
        LogSource.LogInfo($"Wildcard translation entries: {WildcardKeys.Count}");
    }

    private static Regex? BuildWildcardRegex(string key)
    {
        var sb = new StringBuilder("^");
        var parts = key.Split('*');
        for (int i = 0; i < parts.Length; i++)
        {
            sb.Append(Regex.Escape(parts[i]));
            if (i < parts.Length - 1)
                sb.Append(i == parts.Length - 2 ? "(.*)" : "(.*?)"); // 最后一个贪心，前面的非贪心
        }
        sb.Append('$');
        try { return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.Singleline); }
        catch { return null; }
    }

    /// <summary>尝试用通配符词条匹配文本；命中则把译文里的 * 依次替换为捕获内容。未命中返回 null。</summary>
    private static string? TryWildcardMatch(string text)
    {
        if (text.IndexOf('*') >= 0)
            return null;
        if (!wildcardReady)
            BuildWildcardCache();
        foreach (var kv in WildcardKeys)
        {
            var m = kv.Value.Match(text);
            if (!m.Success)
                continue;
            var result = kv.Key; // 译文模板（内含 *）
            for (int i = 1; i < m.Groups.Count; i++)
                result = ReplaceFirst(result, "*", m.Groups[i].Value);
            return result;
        }
        return null;
    }

    private static string ReplaceFirst(string s, string old, string newVal)
    {
        int idx = s.IndexOf(old, StringComparison.Ordinal);
        if (idx < 0)
            return s;
        return s.Substring(0, idx) + newVal + s.Substring(idx + old.Length);
    }

    /// <summary>极简反转义，兼容 XUnity.AutoTranslator 的常见转义。</summary>
    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;

        return s
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\=", "=");
    }

    /// <summary>匹配前统一换行符：词条文件多为 LF，游戏内 XML/场景文本常为 CRLF。</summary>
    private static string NormalizeNewlines(string s)
    {
        return s.IndexOf('\r') >= 0 ? s.Replace("\r\n", "\n").Replace("\r", "\n") : s;
    }
}
