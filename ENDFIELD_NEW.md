# `endfieldNew` — 终末地（现网版本）资源导出

现网《明日方舟：终末地》的资源包与 AnimeStudio 内置的 `Arknights Endfield` 分支不再兼容：
Shader 容器从 `m_UseExternalBlobs` + `PPtr<SubShaderBinaryData>` 换成了
`m_EnableShaderLODStreaming` + `subShaderBlobs`（程序块内嵌、按 ShaderLOD 分片）。
旧的 `Arknights Endfield` 选项会在 `Shader.cs` 里抛 `EndOfStreamException`。

本仓库在原有功能之上新增了一个独立的 `endfieldNew` 游戏类型来解析这套新布局，
**不改动**原 `Arknights Endfield` 的行为。

## 用法

```bash
dotnet AnimeStudio.CLI.dll <输入.ab 或目录> <输出目录> \
    --game endfieldNew \
    --types Shader \
    --export_type Convert
```

`--game` 还接受别名 `endfield`（= 老的 `ArknightsEndfield`）。

## 改动清单（相对上游）

| 文件 | 改动 |
|---|---|
| `AnimeStudio/GameManager.cs` | 新增 `GameType.ArknightsEndfieldNew`、显示名 `Arknights Endfield (New)`、`IsArknightsEndfieldNew()`、别名表 `GameAliases`（`endfieldNew` / `endfield`）；`GetGameNames()` 也返回别名。 |
| `AnimeStudio/Crypto/VFSUtils.cs` | 所有 `case GameType.ArknightsEndfield:` 处补上 `ArknightsEndfieldNew`，否则容器校验会报 `Not a VFS file`。 |
| `AnimeStudio/Classes/Shader.cs` | 新增 `SubShaderBlob` 与 `m_EnableShaderLODStreaming` / `subShaderBlobs` 字段及解析分支。`m_CompressedBlob` 是任意长度字节数组，读完必须 `AlignStream()` 再读后面的 `uint[][]`。 |
| `AnimeStudio.Utility/ShaderConverter.cs` | 每个 LOD 各自一张程序索引表，所以按 `m_SubShader.m_LOD` 解析出对应的 `ShaderProgram[]`（`ShaderProgramSet`）；Pass 的变体位于 `m_PlayerSubPrograms`（`m_SubPrograms` 为空）。 |
| `AnimeStudio.Utility/Smolv/SmolvDecoder.cs` | 支持 SMOL-V **revision 1**：`MemberDecorate` 游程打包、`Decorate` 目标 zig-zag、delta-from-result 一律 zig-zag；并修正输出的 SPIR-V 版本字（去掉 rev 字节）。 |
| `AnimeStudio.Utility/Smolv/SpvOp.cs`、`OpData.cs` | opcode 表从 331 扩到 367 项。终末地 Vulkan 模块用到 `GroupNonUniform*`（333/338/360…），缺表会直接失步。 |
| `AnimeStudio.Utility/CSspv/OperandType.cs` | **修上游既有 bug**：`EnumType.ReadValue` 把枚举参数读成绝对下标 `1 + n` 而不是 `index + 1 + n`，导致所有 `OpDecorate` 的参数等于指令第一个操作数（`OpDecorate %16 DescriptorSet 0` 被反汇编成 `DescriptorSet 16`）。 |
| `AnimeStudio.Utility/SpirvCross.cs` | 新增：调用外部 `spirv-cross` 把 SPIR-V 反编译成 GLSL。 |
| `AnimeStudio.Utility/SpirVShaderConverter.cs` | 新增 `ConvertToGlsl()`：按 snippet 表逐阶段解 SMOL-V → SPIR-V → GLSL，并对每段加 `// ---- vert ----` 之类的小标题。 |

## 输出形态：GLSL（不是 SPIR-V 汇编）

每个 `SubProgram` 现在是 spirv-cross 产出的 GLSL：

```
  GpuProgramID 139717
Program "vp" {
SubProgram "vulkan " {
Keywords { "HG_ENABLE_MV" }
"// hash: 20915786233e9894
// ---- vert ----
#version 450
layout(set = 0, binding = 12, std140) uniform _TransformVariables{ ... } _TransformVariables;
...
// ---- frag ----
#version 450
...
"
}
}
```

### uniform 名字还原（保守策略）

反编译产物里的匿名标识符（`_15_16 { ... } _16;`、成员 `_m0`、纹理 `_60`）现在会
按**序列化数据里可精确对应的资源名**改写（`AnimeStudio.Utility/ShaderUniformMap.cs`）：

| GLSL 内容 | 证据来源 |
|---|---|
| uniform 块名 / 实例名 | SPIR-V `DescriptorSet`+`Binding` 装饰 ↔ `m_DescriptorSetParams`（或 `m_ConstantBufferBindings` 等 `(flags<<24)\|(set<<16)\|binding` 打包寄存器），优先本 Pass 参数，其次全 shader 联合表 |
| cbuffer 成员名 | SPIR-V `OpMemberDecorate Offset` ↔ `m_ConstantBuffers` 成员字节偏移，精确相等才替换 |
| 未序列化绑定的 cbuffer（运行时绑定） | 成员字节偏移布局与某个已知 cbuffer **完全相等**且无歧义时才命名（如部分 UnityPerMaterial） |
| 纹理 / 采样器 | 同 (set, binding) 精确命中 |

改用 `--vulkan-semantics` 优先输出：image/sampler 保持独立、`layout(set=N, binding=M)`
直接可见，避免 GL 语义把纹理合并成无法对应资源名的 `SPIRV_Cross_Combined*`。

对应不上的（被裁剪的 partial cbuffer、未序列化的内部 set 等）一律保留匿名名——
宁可匿名，不给错误名字。`ANIMESTUDIO_SHADER_DEBUG=1` 打印未解析项的 (set, binding)。

`spirv-cross` 按以下顺序查找：

1. 环境变量 `SPIRV_CROSS=<可执行文件路径>`；
2. 常见安装位置 —— `C:\Program Files\RenderDoc\plugins\spirv\spirv-cross.exe`、`C:\VulkanSDK\Bin\spirv-cross.exe`、`/usr/bin/spirv-cross` 等；
3. `PATH`。

找不到时会退回 SPIR-V 汇编，并在日志里给出 `Warning`。

反编译的着色器调试名在编译期已被剥离，所以临时变量是 `_1234`、uniform 块成员是 `_m7`；
**binding 是保留的**，例如 `layout(binding = 12, std140) uniform _16_17` 就是 `UnityPerMaterial`。

## 环境变量

| 变量 | 作用 |
|---|---|
| `SPIRV_CROSS` | 指定 spirv-cross 可执行文件路径。 |
| `ANIMESTUDIO_SHADER_ASM=1` | 强制输出 SPIR-V 汇编（旧行为），不做 GLSL 反编译。 |
| `ANIMESTUDIO_SHADER_VARIANTS=first` | 每个 Pass / stage 只写第一个关键字变体。 |

## Unity 格式导出（`--export_type UnityAssets`）

新增导出类型，把资源直接写成 Unity 原生资产格式并互相挂钩，输出目录可整体拷进
Unity 工程的 `Assets/` 使用：

| 类型 | 产物 | 说明 |
|---|---|---|
| Material | `Material/*.mat` | YAML 材质，`m_Shader` 与 `m_TexEnvs` 以 GUID 引用导出的 shader / 贴图 |
| Mesh | `Mesh/*.asset` | 顶点/索引/blendshape 数据按 Unity 2021.3 序列化格式原样回写（游戏即 2021.3，无损） |
| AnimationClip | `AnimationClip/*.anim` | 复用现有动画转换器的 YAML 输出 |
| GameObject / Animator | `Prefab/*.prefab` | 完整 Transform 层级 + MeshFilter / MeshRenderer / SkinnedMeshRenderer / Animator / Animation 组件 |
| Texture2D / Sprite | `Texture2D/*.png` 等 | 走原有贴图导出 |
| Shader | `Shader/*.shader` | 反编译文本（Unity 无法当编译版 Shader 导入，仅作阅读/引用占位） |

每个产物旁都有确定性 GUID 的 `.meta`（MD5(source file + pathID)，重跑引用不变）。
引用只有**有证据**时才写：目标已（或会被）导出时写 `{fileID, guid, type: 2}`，
否则 `{fileID: 0}`；被引用但没在 `--types` 里的贴图/材质等会自动拉入导出。

注意：

- `--types` 传多个类型时逗号分隔现在可用（`--types Material,Mesh,Texture2D,GameObject,Shader`）。
  引用要闭合，请把 `Shader` 也加进列表。
- 跨 bundle 引用只在该 bundle 同批加载时才能解析（与 CLI 一贯行为一致）。
- Animator 的 `m_Controller`（AnimatorController，通常为 MonoBehaviour）不在导出范围内，
  引用会留空；Animation 组件的 clip 列表正常链接。

## 环境变量（原文） |

## 体积：为什么大，怎么变小

实测 `HGRP/Effect/VFXBaseV2`（4 个 SubShader × 3206 个关键字变体 × vert+frag）：

| 模式 | 大小 | 说明 |
|---|---|---|
| SPIR-V 汇编，全部变体 | **794 MB** | 修改前的行为 |
| GLSL，全部变体 | **85 MB** | 默认；3206 个 SubProgram、6412 段 GLSL，0 失败 |
| GLSL，`VARIANTS=first` | **73 KB** | 每个 Pass 只留首个变体（3 个 Pass × 2 阶段） |

也就是说体积主要来自**变体数量**（3206 个），换 GLSL 只降约 9 倍。
需要能和 Phase 1 对齐的小文件时，用 `ANIMESTUDIO_SHADER_VARIANTS=first`。

## 已知限制

- LOD600 的 `subShaderBlobs` 只有 5 字节（空），对应的 SubShader 没有 Pass，属正常。
- 只导出 shader 时耗时约 3.5 分钟（瓶颈是每个 LOD 都要解压并解析完整的程序索引表，
  与最终打印多少变体无关）。
- 若游戏换版本：先用 `--types Shader:Export --export_type Dump` 看 TypeTree，
  确认 `subShaderBlobs` 的字段顺序与 `m_CompressedBlob` 的对齐要求是否变化。
