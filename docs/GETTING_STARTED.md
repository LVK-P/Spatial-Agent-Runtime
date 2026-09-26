# 快速入门

## 1. 环境准备

与宿主无关的代码层以 `.NET 8` 为目标框架。完整解决方案还需要 ArcGIS Pro 3.4 及版本匹配的 ArcGIS Pro SDK 程序集。

推荐环境：

- Windows 10 或 Windows 11 x64
- .NET 8 SDK
- Visual Studio 2022
- ArcGIS Pro 3.4
- ArcGIS Pro SDK for .NET

如果 ArcGIS Pro 未安装在 `C:\Program Files\ArcGIS\Pro\`，请设置：

```powershell
$env:ARCGISPRO_INSTALL_DIR = 'D:\Apps\ArcGIS\Pro\'
```

该值必须以目录分隔符结尾。

## 2. 还原与构建

构建可移植的 Runtime 与 MCP 层：

```powershell
dotnet restore src/runtime/SpatialAgent.Runtime.csproj
dotnet build src/runtime/SpatialAgent.Runtime.csproj -c Release --no-restore
dotnet build src/adapters/Mcp/SpatialAgent.Adapters.Mcp.csproj -c Release
```

如果只评估 Runtime 架构，不需要安装 ArcGIS Pro，也不需要生成本地工具元数据。

### 生成可选的本地工具元数据

公开仓库不附带从 ArcGIS 安装环境生成的工具目录。需要启用扩展 GP 工具时，请先确认当前账户拥有有效的 ArcGIS Pro 使用许可，再在 ArcGIS Pro Python Command Prompt 中运行：

```powershell
python tools\generate_local_arcgis_metadata.py
```

脚本通过公开的 ArcPy 接口读取可调用工具与参数结构，只生成运行所需的接口事实，不复制帮助正文、说明文本或参数默认值。输出写入被 Git 忽略的 `local-assets/`：

- `tools_manifest.json`——本机可调用接口与参数结构；
- `tools_index.json`——本地检索索引；
- `tool_risk_catalog.json`——可选，由使用者自行审查和维护，不会自动生成。

生成器将所有动态工具默认标为 `Destructive`。只有完成本地风险审查后，才应通过本地风险目录降低级别。若不希望资产位于仓库工作树，可设置 `SPATIAL_AGENT_LOCAL_ASSETS_DIR` 指向外部目录。

在 ArcGIS Pro 工作站上构建完整 Runtime：

```powershell
dotnet restore SpatialAgent.RuntimeOnly.slnx
dotnet build SpatialAgent.RuntimeOnly.slnx -c Release --no-restore
dotnet test SpatialAgent.RuntimeOnly.slnx -c Release --no-build
```

由于 Esri 桌面目标不支持 `dotnet build`，Add-In 打包必须使用 Visual Studio MSBuild：

```powershell
msbuild apps/ArcGISPro/AddIn/SpatialAgent.ArcGISPro.AddIn.csproj /t:Build /p:Configuration=Release
```

生成的 `.esriAddinX` 文件已被 Git 忽略。

## 3. 配置 LLM

1. 启动 ArcGIS Pro 并打开一个项目。
2. 从 Add-In 选项卡打开 Spatial Agent 窗口。
3. 输入兼容 DeepSeek 协议的 API 密钥。
4. 保存并执行连接测试。

密钥使用 Windows DPAPI 在 `CurrentUser` 作用域下加密，不会写入源码、日志或项目记忆。请勿向仓库添加本地凭据文件。

## 4. Runtime 资源

公开源码包含：

- `skills/`——面向任务的 Runtime 指令；
- `knowledge/`——本地 GIS 参考资料。

`local-assets/` 中的 JSON 是可选的本地运行资产，不属于公开发行内容。没有这些文件时，核心工具、Agent Runtime 与安全机制仍可工作，但动态 GP 工具发现不可用。构建 Add-In 时，MSBuild 仅在这些文件存在时将其复制到输出目录。

## 5. CI 模型

公开工作流只运行 `portable-layers`，在 GitHub 托管的 Windows Runner 上构建与宿主无关的 Runtime/MCP 项目。完整 ArcGIS Pro 集成测试由维护者在已获许可、与公网仓库隔离的工作站本地执行。

不在公共仓库中挂接 ArcGIS Pro 自托管 Runner，可以避免公开工作流接触商业软件安装、许可证状态、本地项目和持久凭据，也不会造成公共 CI 已验证厂商集成的误导。

## 6. 常见问题

**找不到 ArcGIS 程序集**
设置 `ARCGISPRO_INSTALL_DIR`，或通过 `/p:ArcGISProInstallDir=...` 将路径传给 MSBuild。

**构建 Add-In 时，`dotnet build` 报告 MSB4801**
请使用 Visual Studio MSBuild。这是 Esri 目标系统的要求。

**运行时缺少工具**
核心工具可直接使用。若缺少动态 GP 工具，请在本机运行生成脚本，并确认构建输出旁存在 manifest 与索引。不要把生成结果提交到仓库。

**将配置复制到另一个 Windows 账户后，API 密钥失效**
DPAPI 加密与用户绑定，请在新账户下重新输入密钥。
