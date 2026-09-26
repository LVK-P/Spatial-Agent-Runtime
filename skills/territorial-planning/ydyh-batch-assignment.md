# 用地用海代码批量分配

## 触发条件
- "用地用海"、"YDYH"、"ydyhmc"、"ydyhbm"、"分类代码"、"地类编码"
- "分配代码"、"填代码"、"批量填地类"、"名称转代码"、"代码转名称"
- "用地分类标准"、"2023分类"、"国土空间用地用海"
- "三调转用地用海"、"代码转换"

## 前置知识

**分层策略（2026-07-19 用户裁定：读不读取决于唯一值数量）**：

1. `agent_describe_layer` 拿到 distinct_count（唯一值数量）
2. **唯一值 ≤ 10**：可先 `cc_read_rows` 抽样核对（按 source_field 的类别 each 一条 where_clause 限定），
   手动构造映射字典后用 `management_CalculateField` 写入
3. **唯一值 > 10**：直接用 `cc_map_field_values`（内置唯一值扫描→官方对照→code_block→CalculateField→验证）——
   逐行读表不可行，类别必须合并
4. **无论哪条路径，最后一步必须抽样验证**——`cc_map_field_values` 自带 verify_field_populated，
   手动路径也必须调 `management_SelectLayerByAttribute` 查空值率或抽样回查 `cc_lookup_land_code`

单点查询或人工核对用 `cc_lookup_land_code`。**严禁凭记忆写代码，严禁从其他图层猜测映射**。

## 所需工具
- cc_map_field_values
- cc_lookup_land_code
- agent_describe_layer
- management_SelectLayerByAttribute
- analysis_Statistics

## 执行流程

### 步骤 1：确认目标图层和基准字段

**Agent 操作**：
1. 用 `agent_describe_layer` 查看图层字段与样本值（确认依据字段确实有值——样本全空则停止并说明）
2. 现状数据默认三调分类（sd），规划/用地用海字段用 2023 版（yh）；不确定先问用户

### 步骤 2：执行映射赋值

**Agent 操作**：
1. 调 `cc_map_field_values`：in_table=图层、source_field=依据字段（如 YDYHMC）、
   field_name=目标字段（如 YDYHBM）、mapping_source 按体系与方向选（如 yh_name_to_code）
2. 读返回结果：mapped_pairs 组映射已写入；**unmatched（无官方对应）与 ambiguous（一名多码）
   的行保留了原值——必须向用户逐项转告，不得含糊**
3. ambiguous 项如需落定：用 `cc_lookup_land_code` 查候选，结合地块名称/规划用途与用户确认后，
   以显式 mapping 参数二次调用 `cc_map_field_values` 补齐

### 步骤 3：验证分配结果

**Agent 操作**：
1. `management_SelectLayerByAttribute` 选目标字段为空的记录，报告空值数量（选完即清 CLEAR_SELECTION）
2. `analysis_Statistics` 按新代码统计面积做合理性检查（输出裸名 tmp_ 前缀）

## 输出确认清单
- [ ] 全部映射来自 cc_lookup_land_code 查询结果，无一编造
- [ ] unmatched 项已如实告知用户
- [ ] 目标字段空值率已报告
- [ ] 按代码的面积汇总已产出

## 实际踩坑记录

### 坑1：三调代码一对多映射
三调 `05` 在用地用海中可能对应多个子类。`cc_lookup_land_code`（system=sd）会在 yh_suggestions
里列出全部候选——结合地块名称、规划用途选择，并在汇报中说明选择依据。

### 坑2：同名不同码
"农村宅基地"在用地用海是 0703、在三调是 0702——**先确认用户要哪套体系**，凭记忆必错。

### 坑3：可调整地类的 K 后缀
三调带 K 后缀代码（如 0201K 可调整果园）转换时单独标记为"可调整"类型。

### 坑4：空映射会清空用户数据（2026-07-18 事故）
表达式若以 None 兜底，映射缺失时会把目标字段整列写空。必须 `.get(!KEY!, !目标字段!)` 保值；
后置验证（verify_field_populated）会拦截全空写入，届时如实报告失败。
