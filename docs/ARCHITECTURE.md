# 系统架构

## 设计目标

Spatial Agent Runtime 将概率性的规划能力与确定性的执行权分离。模型可以理解意图并提出工具调用；Runtime 则负责决定哪些操作可以执行、哪些资源属于目标范围、什么证据能够成立，以及何时才算真正完成目标。

## 分层结构

```text
Contracts  ← 接口、DTO 与稳定的跨层语义
    ↑
Core       ← 纯安全策略、身份策略、参数策略与规划策略
    ↑
Runtime    ← Agent 循环、上下文、物理规划、调度与完成判定
    ↑
Services / Tools / Knowledge / Adapters
    ↑
Apps       ← ArcGIS Pro 组合根
```

依赖规则由自动化测试约束：

- Contracts 不依赖任何具体实现；
- Core 只依赖 Contracts 与通用基础包；
- Runtime 只依赖 Contracts 与 Core，不依赖 ArcGIS、WPF 或模型供应商 SDK；
- ArcGIS、LLM、持久化和 UI 相关实现保留在 Adapters、Services 与 Apps 层。

## 五个核心闭环

### 1. 意图 → 目标合同

用户意图会被编译为语义化合同，其中包含资源绑定、量词、交付物、必要能力、参数约束与证明义务。后续生成的物理计划不能静默缩小或重新定义这份合同。

### 2. 计划 → 可采纳性检查 → 执行

Planner 提出工具调用。确定性组件负责规范化名称与默认值、解析调用影响、验证目标覆盖、拒绝合同范围外的写入，并决定下一批操作应执行、探查还是请求澄清。

### 3. 执行 → 验证 → 完成

`StepExecutionResult` 记录每一步的真实状态。后置条件通过读取 ArcGIS 状态完成验证，而不是相信模型文本或 GP 成功消息。目标完成必须同时满足目标闭包与证明闭包，工具成功数量从来不是充分条件。

### 4. 风险 → 暂停 → 恢复

每次调用都根据最终参数确定风险等级。Modify 操作需要评估数据影响范围；Destructive 操作必须先完成备份并获得明确审批。审批票据绑定不可变调用、目标与宿主状态修订版本，过期审批不能授权已经发生变化的操作。

### 5. 会话 → 对账 → 恢复

消息、计划、观察、验证与账本会写入 Checkpoint。项目 Session Lease 用于区分活动、已释放和中断会话。后续进程可以将失去所有权的孤立运行收敛为明确终态，但不会伪造成功完成记录。

## Runtime 核心流程

```text
收集上下文
  → 编译或修补目标合同
  → 选择工具表面
  → 调用模型
  → 验证工具调用协议
  → 编译物理计划
  → 执行安全与宿主修订检查
  → 执行工具
  → 验证后置条件
  → 更新目标与证明证据
  → 继续、暂停、失败或生成确定性报告
```

当模型返回语义空响应时，Runtime 只允许一次有界恢复尝试。如果仍然失败，Runtime 会基于执行账本与证明状态生成持久化报告，而不会把空响应描述成“已完成”。

## 关键模型

- `ResolvedGoalScope`：当前语义边界与资源身份；
- `AgentPlan` / `PlanStep`：带版本的物理工作与依赖关系；
- `InvocationEffect`：最终读写范围、可逆性与审批语义；
- `ApprovalTicket`：不可变的授权证据；
- `StepExecutionResult`：执行、验证与状态转换结果；
- `GoalCompletionReport`：面向用户的确定性对账报告；
- `AgentRunState`：持久化 Checkpoint 投影；
- `SessionLease`：会话所有权与崩溃恢复事实。

## 扩展点

- 通过 `IGisReader`、工具执行器和后置条件探针接入新的 GIS 宿主；
- 通过 `ILLMProvider` 接入新的 LLM，无需修改 Runtime；
- 通过版本化 Skills、Knowledge 与能力声明增加领域行为；
- 通过 `IReadOnlyToolGateway` 增加传输层，外部客户端永远不会直接获得进程内可变工具注册表。

当前 ArcGIS Pro Add-In 只是一个组合根，而不是产品的架构边界。
