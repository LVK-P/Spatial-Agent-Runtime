# 字段驱动批处理模式

## 适用场景
当用户要求"按某字段分级/分类做某操作"时，例如：
- "按等级生成不同宽度缓冲区"
- "按地类编码分别统计面积"
- "按行政区划融合地块"

## 核心原则
**字段驱动 > 选择驱动**

❌ 错误做法（选择驱动）：
```
SelectLayerByAttribute(level=1) → Buffer(50m)
SelectLayerByAttribute(level=2) → Buffer(30m)
SelectLayerByAttribute(level=3) → Buffer(15m)
```
问题：耗时长、需要多轮、容易遗漏、选择集相互干扰。

✅ 正确做法（字段驱动）：
```
1. agent_describe_layer → 查看字段值分布
2. management_AddField → 添加距离字段（如 buf_dist, DOUBLE）
3. management_CalculateField → 按等级计算距离值
   表达式: reclass(!level!, 1, 50, 2, 30, 3, 15)
4. analysis_Buffer → 用 buf_dist 字段作为距离，dissolve_option="ALL"
```

## Python 表达式模板

### 按等级赋值（reclass 模式）
```
def reclass(val, *pairs):
    for i in range(0, len(pairs), 2):
        if val == pairs[i]: return pairs[i+1]
    return 0
```
在 CalculateField 中使用: `reclass(!level!, 1, 50, 2, 30, 3, 15)`

### 按条件赋值（if-else 模式）
```
50 if !level! == 1 else (30 if !level! == 2 else (15 if !level! == 3 else 0))
```

### 按编码映射（字典模式）
```
{1: 50, 2: 30, 3: 15}.get(!level!, 0)
```

## 执行流程
1. 先用 `agent_describe_layer` 看字段的值分布
2. 确认哪些字段可以作为分级依据（distinct_values 较少且有意义）
3. 添加结果字段（DOUBLE 类型）
4. 用 CalculateField + Python 表达式一次性赋值所有行
5. 执行 GP 工具（Buffer/Dissolve/Statistics），用步骤2的结果字段作为参数
6. 验证结果

## 注意事项
- 永远不要对同一个图层反复 SelectLayerByAttribute 然后执行相同操作
- 如果字段值很多（distinct_count > 10），说明不适合直接分级，考虑先分类聚合
- CalculateField 之前确保字段已存在（或用 cc_calculate_area/cc_calculate_length 一步完成）
