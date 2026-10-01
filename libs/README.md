# libs/ 放什么

构建需要 4 个引用程序集。它们是第三方框架二进制，不入库，从你手上的 BepInEx 6 里拷过来：

```
libs/0Harmony.dll
libs/BepInEx.Core.dll
libs/BepInEx.Unity.IL2CPP.dll
libs/Il2CppInterop.Runtime.dll
```

这 4 个文件都在 **BepInEx 6（IL2CPP 版）** 解压后的 `BepInEx\core\` 目录里，
本仓库按 `6.0.0-be.788` 编译；同系列预览版一般也能编过。

汉化插件**不引用游戏的 interop 程序集**（只挂 Harmony 补丁），所以这里不需要装过游戏、
生成过 `BepInEx\interop\` 的目录 —— 同作者的 Sprocket-Tools 仓库需要，这里不需要。
