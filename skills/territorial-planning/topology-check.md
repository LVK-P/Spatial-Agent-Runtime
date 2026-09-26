# 拓扑检查（压盖/空隙/标准拓扑）

## 触发条件
- "拓扑"、"拓扑检查"、"拓扑分析"、"拓扑错误"、"拓扑规则"
- "压盖"、"重叠检查"、"自相交"、"空隙"、"缝隙"、"狭长面"
- "数据质检"、"图斑检查"、"要素重叠"

## 前置知识

**双轨策略（2026-07-19 用户裁定：分数据规模选择路径以控制时间与 token 成本）**：

**第一步必做**：`agent_describe_layer` 获取参检图层的 featureCount（要素数）。

| 要素数 | 路径 | 理由 |
|---|---|---|
| ≤ 50 | 叠加快检（Intersect 自交 + Erase 查空隙） | 小数据量下快检足够精确，省去建数据集/拓扑/规则的 token 开销 |
| > 50 | **标准拓扑链**（建数据集→复制要素→建拓扑→加规则→验证→导出错误） | 大数据量需要规则化验证与错误图层定位，快检的 Intersect 自交在百万级面下不可行 |

**汇报要求**：必须说明选用了哪条路径（"因为要素数为 X，选用了快检/标准拓扑"）。

**注意**：Intersect 自交查压盖时，如果两个面仅顶点/边相切（共线），Intersect 返回的是线或点而非面——此时应报告"未发现面实质重叠"而非"未发现重叠"。Standard Overlap 查的是面，不是点或线。

**硬约束**：拓扑只能建在**要素数据集**内，且数据集坐标系创建即锁死。agent 工作空间
（agentworkspace.gdb）根目录是扁平的——做标准拓扑时按需新建临时数据集，坐标系
**必须取自参检图层**（agent_describe_layer 查 WKID），不要用地图坐标系。
官方指南：https://pro.arcgis.com/zh-cn/pro-app/3.4/help/data/topologies/an-overview-of-topology-in-arcgis.htm

## 所需工具
- management_CreateFeatureDataset
- management_CreateTopology
- management_AddFeatureClassToTopology
- management_AddRuleToTopology
- management_ValidateTopology
- management_ExportTopologyErrors
- management_CopyFeatures
- agent_describe_layer
- analysis_Intersect
- analysis_Erase

## 执行流程（标准拓扑）

### 步骤 1：准备要素数据集

1. `agent_describe_layer` 确认各参检图层坐标系一致（不一致先说明，请用户确认是否投影统一）
2. `management_CreateFeatureDataset`：数据集必须建在 agent 工作空间 gdb 内，`out_dataset_path` 格式为
   `<从上下文获取的实际项目路径>/agentworkspace.gdb`（不要写死路径——项目路径由 agent_describe_layer
   或 agent_get_map_info 返回的工作空间信息中提取，每台机器不同）。
   数据集名：`tmp_topo_<检查名>`，spatial_reference 用参检图层的 WKID。
3. **★ 合层前统一数据源**：任何 Merge / Union / Intersect / Erase 操作前，检查所有输入要素类是否在
   同一个 gdb 内。如果分散在默认工程 gdb 和 agentworkspace.gdb 中，先用 `management_CopyFeatures`
   把全部输入复制到 agentworkspace.gdb（输出裸名，运行时自动路由），再执行合层。
   跨 gdb 的 Union / Merge 在 ArcGIS Pro 中行为不确定（可能报"找不到输出"），此规约普适所有合层场景。
4. `management_CopyFeatures` 把每个参检图层复制进数据集
   （输出写全名：<数据集全路径>/图层名_副本；检查在副本上做，不动用户原数据）

### 步骤 2：创建拓扑并加规则

1. `management_CreateTopology`：在数据集内建拓扑（名如 tmp_topo_检查名_topology），
   cluster_tolerance 默认留空（用要素类默认容差）
2. `management_AddFeatureClassToTopology`：逐个把副本要素类加入（xy_rank 默认 1）
3. `management_AddRuleToTopology` 按需加规则，常用面规则：
   - 压盖（图层内部）：`Must Not Overlap (Area)`
   - 空隙（图层内部）：`Must Not Have Gaps (Area)`
   - 跨图层压盖：`Must Not Overlap With (Area-Area)`（需 in_featureclass 与 in_featureclass2）
   - 线自相交：`Must Not Self-Intersect (Line)`；悬挂点：`Must Not Have Dangles (Line)`

### 步骤 3：验证与导出错误

1. **★ 先确认事实再行动**：Validate 之前必须先用 `agent_describe_layer` 读各错误图层要素数作为证据。
   若 RepairSelfIntersection 或类似修复工具可用——**必须先 Intersect 自交确认存在实质重叠，再调修复；
   无重叠则无需修复**。先诊断再治疗的时序不可倒置。
2. `management_ValidateTopology`（visible_extent 用 Full_Extent）
3. `management_ExportTopologyErrors`：错误导出为点/线/面三个图层（basename 带 tmp_ 前缀）
4. `agent_describe_layer` 读各错误图层要素数——**这是结论的数字依据**

### 步骤 4：汇报

- 按规则逐条报告错误数量与类型；0 错误如实说"未发现违反该规则的要素"
- 说明检查是在副本上做的，错误图层名单列出
- **城乡规划行业判据**：`Must Not Have Gaps` 规则下被标记的"外部空隙"中，完整行政区划面的外边界
  或项目红线边界产生的 Gaps 属于数据本身的合法边界，不是事实意义上的空隙——汇报时**必须**主动
  说明"该空隙是数据边界环，非真实拓扑错误"，并建议用户确认是否需要补全研究范围外的地块。
  其他行业（如交通网络/管线），同样需要结合行业标准判断，而非机械报告拓扑规则输出

## 执行流程（叠加快检，仅临时排查）

1. 压盖：`analysis_Intersect`（同图层自交 in_features 传两次），输出 tmp_ 前缀裸名
2. 空隙：先合并参检面，再 `analysis_Erase` 从范围面中擦除，剩余即空隙候选
3. 汇报时明确"此为叠加快检，非标准拓扑验证；正式质检建议标准拓扑流程"

## 输出确认清单
- [ ] 使用了哪条路径、为什么，已向用户说明
- [ ] 标准拓扑：数据集坐标系与参检图层一致
- [ ] 每条规则的错误数量有 describe 数字依据，无编造
- [ ] 检查副本与错误图层均在 agent 工作空间，未改动用户原数据

## 实际踩坑记录

### 坑1：拓扑必须在要素数据集内
gdb 根目录的要素类不能直接建拓扑。必须先建数据集并复制要素类进去。

### 坑2：数据集坐标系锁死
数据集创建后坐标系不可改，与成员要素类必须一致——用参检图层的 WKID 建，
不要用地图的（地图可能是 Web Mercator 而数据是 CGCS2000）。

### 坑3：Must Not Have Gaps 的外边界假阳性
该规则把数据外边界闭环报为一处 gap，属预期行为，汇报时要排除说明。

### 坑4：ValidateTopology 之前错误表是空的
AddRule 后必须 Validate，否则 ExportTopologyErrors 导不出任何东西。
