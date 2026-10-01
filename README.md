# Sprocket 中文汉化插件（SprocketTranslation）

Sprocket的简体中文汉化插件，
运行在 **BepInEx 6（IL2CPP 版）** 上，**纯 Harmony 挂点，不做任何类型注入**。

作者 / 维护： Sevenight。许可证 MIT，见 `LICENSE`。

## 仓库范围

- **包含**：插件本体 `Plugin.cs`（单文件）、`SprocketTranslation.csproj`、词条格式说明 `docs/ENTRIES.md`。
- **不包含**：**全部翻译词条**。词条是本项目的主要工作成果，不随仓库分发；发行包中它们以密文
  `entries.bin` 嵌在 DLL 内。仓库也不包含生成词条与出包的打包链（合并、加密、安装器）。
- 因此仓库**可以直接编译**，产物不含词条 —— 插件会输出一行
  `Embedded translations resource missing; only external .txt entries will load.` 后照常工作；
  将自有词条放入外部 `Translations\*.txt` 即可生效（格式见 `docs/ENTRIES.md`）。

关于词条加密的说明：发行包的加密**只能防随手拷走**，反编译即可提取；源码公开后，解密逻辑与密钥就写在
`Plugin.cs` 里，这层加密对能读代码的人无效。它仍能挡住"复制 txt 改个署名"这类最常见的行为，
因此予以保留，但它不是安全机制，不应被当作安全机制看待。

## 仓库文件

| 文件 | 作用 |
| --- | --- |
| `Plugin.cs` | 插件本体，单文件：配置项注册、逐个方法手工 `harmony.Patch` 的 TextMeshPro 文本挂点、词条表的装载与查找顺序（精确 → 数字模板 → 旧模板 → 通配）、中文字体加载与回退、采集模式写 `_ExtractedText.txt` |
| `SprocketTranslation.csproj` | 构建脚本：只引用 `libs/` 的 4 个框架程序集，不引用游戏类型；`entries.bin` 存在则作为嵌入资源打进 DLL，缺失时照常构建 |
| `docs/ENTRIES.md` | 外部词条文件的格式说明：`原文=译文` 的分隔规则、`\n \r \t \=` 四种转义、`{{A}}` 数字模板、`*` 通配、目录加载顺序与常见坑 |
| `libs/README.md` | 构建所需 4 个框架 DLL 的来源与放置位置；这些第三方二进制不入库 |
| `LICENSE` | MIT 许可与版权声明 |
| `.gitignore` / `.gitattributes` | 排除 `bin/`、`obj/`、`libs/*.dll` 与词条密文 `entries.bin`；统一文本行尾 |

## 编译

前置：.NET 6 SDK，以及 `libs/` 里的 4 个框架 DLL（见 `libs/README.md`，从 BepInEx 6 的 `BepInEx\core\` 拷）。
**不需要**装过游戏或生成过 interop —— 本插件只引用框架程序集，不引用游戏类型。

```
dotnet build SprocketTranslation.csproj -c Release
```

产物 `bin\Release\net6.0\SprocketTranslation.dll`。

## 部署

```
<游戏根目录>\BepInEx\plugins\SprocketTranslation.dll
<游戏根目录>\Fonts\Alibaba-PuHuiTi-Medium.ttf     ← 默认字体方案，见下面 FontFile
```

游戏需要先装 BepInEx 6（IL2CPP 版）。首次启动会校验/生成 interop，加载偏慢，属正常。

## 配置

`BepInEx\config\dev.sprocket.translation.cfg`（首次启动生成）。插件 ID `dev.sprocket.translation`。

| 节 / 键 | 默认 | 作用 |
|---|---|---|
| `General.TranslationEnabled` | `true` | 关掉后显示游戏原文，但**仍加载中文字体**（只想支持中文输入、不要汉化的场合） |
| `General.ExtractionMode` | `false` | 把未翻译的原文（数字换成 `{{A}}` 占位符）追加到 `Translations\_ExtractedText.txt`，用来补词条 |
| `General.FormatHooksEnabled` | `true` | 是否钩住 TMP 的 `SetText(string, float/bool…)` 格式重载，用来翻译 `Weight: {0}` 这类动态文本；怀疑界面卡顿时可单独关掉排除 |
| `Font.FontFile` | `Alibaba-PuHuiTi-Medium.ttf` | 中文字体来源：TTF 文件名/路径、Unity 6 TMP 字体资源包名，或系统字体名（系统动态字体在部分机器会缺字变方框，不推荐） |
| `Font.FallbackFontNames` | `Microsoft YaHei,SimHei,SimSun,NSimSun,Noto Sans SC` | 上面加载失败时依次尝试的系统字体名 |
| `Font.ReplaceDefaultFont` | `false` | `false` = 只做回退（推荐，英文原文保持官方字形外观）；`true` = 替换 TMP 默认字体，全局用汉化字体渲染 |

## 已知限制与注意事项（修改代码前请先阅读）

- **IL2CPP interop 里凡是走 `Span`/`ReadOnlySpan` 的 Unity 静态 API 都可能抛出无法 catch 的 AccessViolation，直接闪退游戏**。
  字体加载路径上曾出现过一次：`AssetBundle.LoadFromMemory(Il2CppStructArray<byte>)` 在
  `il2cpp_gchandle_get_target` 崩溃，且 `try/catch` 无法拦截。当前实现只对非字体扩展名进行 bundle 探测。
- 挂点是逐个方法手工 `harmony.Patch`（见 `ApplyPatch`），**不是**按类 `PatchAll`。每个成功的钩子输出一行
  `Hooked 类型.方法`，失败的输出 `Hook 类型.方法 failed: …` 后继续 —— 单个方法挂接失败不会影响其他方法。
  修改挂点后，可通过这两行日志确认实际装载情况。

## 许可与署名

MIT。© 2026 Sevenight。翻译词条不随仓库分发，但**任何再分发请保留本仓库的署名与许可声明**。
Sprocket 是其所有者的商标，本项目与官方无关。
