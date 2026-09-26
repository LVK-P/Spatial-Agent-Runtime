# Spatial Agent Runtime

**面向 ArcGIS Pro 的可信 GIS Agent Runtime。**

Spatial Agent Runtime 让大模型能够规划、执行并验证空间数据操作，同时将安全边界、人工审批、结果证据与故障恢复保留在确定性的 Runtime 中。

当前版本面向 ArcGIS Pro 3.4、Windows 与 .NET 8，包含可运行的 Add-In、分层 Runtime、可选的本地工具发现机制以及完整的自动化测试体系。

## 1. 项目解决什么问题

普通 GIS 助手可以解释工具和生成步骤，却很难对真实数据操作负责：

- 模型可以提出计划，但不能自行定义安全边界；
- 工具返回成功，不代表用户目标已经完成；
- 批量修改必须明确影响范围、审批对象与备份状态；
- 最终回复必须服从实际执行结果与验证证据；
- ArcGIS Pro 崩溃、模型空响应或会话中断后，状态仍需可追踪、可恢复。

本项目将 LLM 用于意图理解、规划与解释，将目标约束、风险判断、工具执行、结果验证和状态持久化交给 Runtime。

| 能力 | 当前实现 |
|---|---|
| GIS 工具接入 | 内置核心工具 + 由使用者在本机生成的可选 GP 接口 manifest |
| Agent 循环 | 有界 ReAct、多轮工具调用、失败反馈与确定性停止条件 |
| 目标约束 | 目标范围、能力、参数和证明义务独立于模型生成的物理计划 |
| 安全控制 | ReadOnly / Increment / Modify / Destructive 四级风险模型 |
| 人工审批 | 精确调用或批次审批，绑定调用签名、目标与数据修订版本 |
| 结果验证 | 输出存在性、字段值、空间参考、拓扑与参数语义证明 |
| 持久状态 | Checkpoint、项目记忆、Session Lease 与崩溃后终态恢复 |
| 可观测性 | Transcript、Verification Ledger、Context Snapshot 与 Audit Log |
| 知识系统 | 本地 BM25、GIS 词典、Markdown Skills 与领域知识文档 |
| 外部访问 | 只读 MCP Streamable HTTP 网关 |

## 2. 如何运行

### 环境要求

- Windows 10/11
- .NET 8 SDK
- 安装“.NET 桌面开发”工作负载的 Visual Studio 2022
- ArcGIS Pro 3.4；完整构建还需要 ArcGIS Pro SDK

如果 ArcGIS Pro 未安装在默认位置，请设置：

```powershell
$env:ARCGISPRO_INSTALL_DIR = 'D:\Apps\ArcGIS\Pro\'
```

公开仓库不分发从 ArcGIS 安装环境提取的工具目录、帮助正文或风险目录。需要扩展动态 GP 工具面时，请在自己已获许可的 ArcGIS Pro 环境中生成仅供本机使用的互操作元数据：

```powershell
python tools\generate_local_arcgis_metadata.py
```

构建并测试 Runtime：

```powershell
dotnet build SpatialAgent.RuntimeOnly.slnx -c Release
dotnet test SpatialAgent.RuntimeOnly.slnx -c Release --no-build
```

在 Visual Studio Developer PowerShell 中构建 Add-In：

```powershell
msbuild apps/ArcGISPro/AddIn/SpatialAgent.ArcGISPro.AddIn.csproj /t:Build /p:Configuration=Release
```

Add-In 通过界面接收 LLM API 密钥，并使用当前 Windows 用户作用域下的 DPAPI 加密保存。任何凭据都不应进入仓库。

完整配置、构建和故障排查见[快速入门](docs/GETTING_STARTED.md)。

## 3. 架构如何设计

```text
用户意图
    │
    ▼
目标合同 ── 范围 / 能力 / 参数 / 证明义务
    │
    ▼
计划编译器 ── 可采纳性 / 目标覆盖 / 影响解析
    │
    ▼
安全门 ── 影响评估 / 备份 / 审批 / 修订检查
    │
    ▼
调度器 ── ArcGIS 工具执行 / 取消 / 进度
    │
    ▼
后置条件验证 ── 真实宿主状态 / 参数证明
    │
    ▼
确定性完成报告 ── 模型文本不能抬高证据等级
```

代码按职责分层：

```text
apps/ArcGISPro/AddIn/   ArcGIS Pro WPF 组合根
src/contracts/          稳定接口与 DTO
src/core/               纯策略与确定性领域逻辑
src/runtime/            Agent 循环、规划、调度与完成判定
src/adapters/           ArcGIS、LLM 与 MCP 适配器
src/services/           安全、持久化、日志与工具加载
src/tools/              高层 GIS 工具与基于 schema 的工具
src/knowledge/          检索与 GIS 领域工具组件
knowledge/              Runtime 知识文档
skills/                 Runtime 技能模块
tests/                  单元测试、架构测试与 ArcGIS 集成契约
```

Contracts、Core 与 Runtime 保持单向依赖；ArcGIS、LLM、持久化和 UI 实现位于外层。完整设计见[系统架构](docs/ARCHITECTURE.md)，版本决策见[版本演进](docs/EVOLUTION.md)。

## 4. 如何保证安全可靠

- **最终参数决定风险：** 工具名不能单独决定安全等级，覆盖行为、目标范围与可逆性均参与判断。
- **审批绑定具体操作：** Approval Ticket 绑定调用签名、目标、影响范围与宿主修订版本，状态变化后自动失效。
- **破坏性操作先备份：** Destructive 操作必须具备备份证据和明确审批。
- **以宿主状态验证结果：** Runtime 读取真实 ArcGIS 状态，不以模型叙述或 GP 成功消息替代验证。
- **确定性完成判定：** 只有目标闭包与证明闭包同时满足时，任务才会被报告为完成。
- **状态可恢复：** Checkpoint 与 Session Lease 支持中断检测、终态对账和下次启动恢复。
- **持续回归验证：** 当前包含 521 个自动化测试，并对第三方生成资产隔离、依赖方向和现场故障建立回归约束。

安全模型、证据体系与恢复顺序见[工程可靠性](docs/ENGINEERING_RELIABILITY.md)，漏洞报告方式见[安全策略](SECURITY.md)。

## 5. 当前边界与路线图

当前边界：

- ArcGIS Pro 是目前唯一具有生产级现场验证的宿主适配器；
- 完整构建与测试需要安装 ArcGIS Pro 的 Windows 环境；
- 工具可用性取决于本地 ArcGIS 许可与扩展模块；
- 公开源码只包含核心工具；扩展 GP 接口元数据须由使用者在本机生成，且默认按最高风险处理；
- 当前 LLM 适配器面向兼容 DeepSeek 聊天与工具调用协议的服务；
- 端到端性能仍会受到模型延迟、上下文规模与 GP 冷启动影响。

近期工作：

1. 扩展可重复执行的 ArcGIS Pro 集成场景；
2. 持续审查范围较宽的 Modify 工具及能力声明；
3. 增加 LLM 供应商适配器，并独立评估工具协议行为；
4. 采集跨项目与扩展组合的 P50/P95 延迟；
5. 加强进度、审批与最终回复绑定的 UI 回归覆盖。

完整状态与路线图见[项目状态](docs/PROJECT_STATUS.md)。

## 第三方边界与发布策略

为降低版权、许可与供应链风险，公开版本采用以下边界：

- 不提交 Esri 二进制文件、SDK 程序集、安装包或从本地安装环境批量提取的工具说明；
- 不发布生成的工具 manifest、检索索引、风险目录及厂商工具文档；
- 只在源码中保留实现互操作所必需的公开 API 引用，扩展元数据由使用者在其已获许可的环境中本地生成；
- 未经本地审查的动态工具统一按 `Destructive` 处理，避免生成元数据被误当作安全背书；
- 公共 CI 只验证可移植层，不把带商业软件和本地许可的工作站作为公开自托管 Runner。

这样做是为了让仓库中的 MIT License 清晰覆盖本项目原创源码与文档，而不对第三方软件、商标、文档或本地生成内容作再许可。详细边界见[第三方声明](THIRD_PARTY_NOTICES.md)。本项目与 Esri 不存在隶属、合作、赞助或背书关系；ArcGIS、ArcGIS Pro、Esri 及相关标识的权利归其各自权利人所有。

## 文档导航

1. [快速入门](docs/GETTING_STARTED.md)——安装、配置、构建与运行
2. [系统架构](docs/ARCHITECTURE.md)——分层、执行闭环与扩展点
3. [工程可靠性](docs/ENGINEERING_RELIABILITY.md)——安全、证据、可观测性与恢复
4. [项目状态](docs/PROJECT_STATUS.md)——已验证能力、当前边界与近期工作
5. [版本演进](docs/EVOLUTION.md)——v1 → v2 → v3 的问题与设计决策

协作规范见[贡献指南](CONTRIBUTING.md)。

## 使用声明

> **本项目当前仅用于技术演示与研究验证，尚未完成生产环境所需的安全性、稳定性与合规性验证，请勿用于实际生产或关键业务。**

## 许可证

本项目原创源码与文档采用 MIT License，详见 [LICENSE](LICENSE)。MIT License 不覆盖 Esri 软件、SDK、商标、文档或使用者本地生成的第三方元数据；详见[第三方声明](THIRD_PARTY_NOTICES.md)。
