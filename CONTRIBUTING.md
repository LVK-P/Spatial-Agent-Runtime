# 贡献指南

## 基本原则

- 保持依赖方向：Contracts → Core → Runtime → 能力层 → Apps。
- 优先修正共享契约与通用策略，再考虑增加场景专用分支。
- 将模型输出视为不可信输入，交由确定性规则验证。
- 不得为了让测试通过而削弱审批、备份或证明要求。
- 现场运行中出现可复现故障时，应补充对应的回归测试。

## 开发与验证

可移植层检查：

```powershell
dotnet build src/runtime/SpatialAgent.Runtime.csproj -c Release --warnaserror
dotnet build src/adapters/Mcp/SpatialAgent.Adapters.Mcp.csproj -c Release --warnaserror
```

在 ArcGIS Pro 工作站上执行完整检查：

```powershell
dotnet build SpatialAgent.RuntimeOnly.slnx -c Release --warnaserror
dotnet test SpatialAgent.RuntimeOnly.slnx -c Release --no-build
```

## Pull Request 要求

提交说明应包含：

- 面向用户的故障表现或新增能力；
- 负责该变更的架构层；
- 对安全、审批与恢复机制的影响；
- 新增或更新的测试；
- 是否完成 ArcGIS Pro 现场验证。

请勿提交项目数据、API 密钥、本地路径、Transcript 或生成的 Add-In 安装包。

同样不得提交 `local-assets/` 中生成的工具 manifest、索引、风险目录，不得复制厂商帮助正文、截图、图标、二进制文件或 SDK 程序集。新增第三方依赖或内容时，应在 Pull Request 中说明来源、版本、许可与再分发依据，并同步更新 `THIRD_PARTY_NOTICES.md`。
