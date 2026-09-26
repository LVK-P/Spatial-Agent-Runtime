# 拓扑规则检查指南

## 触发条件
拓扑检查, 拓扑错误, 图斑重叠, 图斑空隙, 线相交, 悬挂点, 伪结点, 图层压盖, 三线互检, 生态红线重叠, 基本农田重叠, 开发边界越界, 道路压盖, 蓝线绿地冲突, 数据质检, 图形检查

## 面图层自检规则

```
规则POLY-01: 图斑不能重叠
  检查: analysis.Intersect(图层, 图层) → 输出重叠区域
  修复: management.Eliminate + editor.Merge
  严重度: error

规则POLY-02: 图斑不能有空隙
  检查: management.CreateFeatureclass(全域范围) + analysis.Erase(全域, 图斑合并结果)
  修复: editor.CreateFeature + editor.Merge
  严重度: warning
```

## 线图层自检规则

```
规则LINE-01: 线不能重叠
  检查: analysis.Intersect(线图层, 线图层) WHERE 不是同一条线
  修复: editor.Merge + editor.Delete
  严重度: error

规则LINE-02: 线不能相交(道路交叉口除外)
  检查: analysis.Intersect(线图层, 线图层)
  修复: editor.Split + editor.Planarize
  严重度: error

规则LINE-03: 不能有悬挂点(道路网络必须连通)
  检查: management.AddRuleToTopology MustNotHaveDangles
  修复: editor.Extend + editor.Snap
  严重度: warning

规则LINE-04: 不能有伪结点(连续线不应中间断开)
  检查: management.AddRuleToTopology MustNotHavePseudoNodes
  修复: editor.Merge
  严重度: warning
```

## 图层间约束规则（三线+）

```
规则CROSS-01: 生态红线 ∩ 永久基本农田 = ∅
  检查: analysis.Intersect(生态红线, 永久基本农田) → 输出应为空 (要素数=0)
  如果重叠: 人工判定调整生态红线或补划基本农田
  严重度: error

规则CROSS-02: 生态红线 ∩ 城镇开发边界 = ∅
  检查: analysis.Intersect(生态红线, 城镇开发边界)
  修复: 人工判定
  严重度: error

规则CROSS-03: 永久基本农田 ∩ 城镇开发边界 = ∅
  检查: analysis.Intersect(永久基本农田, 城镇开发边界)
  修复: 人工判定
  严重度: error

规则CROSS-04: 建设用地 ⊆ 城镇开发边界
  检查: analysis.SymDiff(建设用地, analysis.Clip(建设用地, 开发边界))
  → SymDiff 输出不为空 = 越界
  修复: 调整建设用地范围或调整开发边界
  严重度: error

规则CROSS-05: 道路红线不能压盖其他用地图斑
  检查: analysis.Intersect(道路红线, 用地图层 WHERE 用地类型 NOT IN 交通运输)
  修复: 调整道路线位或用地图斑边界
  严重度: warning

规则CROSS-06: 行政区边界不能裁切图斑
  检查: management.AddRuleToTopology BoundaryMustBeCoveredBy
  修复: 按行政区界线重新裁切
  严重度: warning

规则CROSS-07: 规划用地与现状用地变更必须有迹可循
  检查: analysis.Intersect(规划用地, 现状用地 WHERE 未标记变更)
  修复: 标记变更区域或调整规划
  严重度: warning

规则CROSS-08: 中心城区城市绿线不与建设用地重叠
  检查: analysis.Intersect(城市绿线, 规划用地 WHERE 类型!=绿地)
  修复: 调整绿线边界
  严重度: error

规则CROSS-09: 中心城区城市蓝线内必须为水域
  检查: analysis.Clip(规划用地, 城市蓝线) WHERE 用地类型!=水域
  修复: 调整用地类型为水域或调整蓝线
  严重度: error
```

## 常用检查流程

```
用户说"帮我检查一下数据的拓扑错误":
1. 列出所有面图层 → 对每个面图层执行 POLY-01 + POLY-02
2. 列出所有线图层 → 对每个线图层执行 LINE-01 到 LINE-04
3. 如果存在三线图层 → 执行 CROSS-01 到 CROSS-03
4. 如果存在建设用地+开发边界 → 执行 CROSS-04
5. 汇总错误数和面积 → 输出报告

用户说"只检查三线":
1. 确认存在: 生态保护红线、永久基本农田、城镇开发边界三个图层
2. 两两执行 analysis.Intersect
3. 如果三个 Intersect 输出都是 0 行 → "三线互检通过"
4. 如果有输出 → 列出重叠图斑的 OID、面积、位置
```

## GP 工具对应

| 拓扑规则类型 | 检查工具 | 修复工具 |
|------------|---------|---------|
| MustNotOverlap(面) | analysis.Intersect(自相交) | management.Eliminate |
| MustNotHaveGaps | analysis.Erase | editor.CreateFeature |
| MustNotOverlap(线) | analysis.Intersect(自相交) | editor.Merge |
| MustNotIntersect | analysis.Intersect | editor.Split + Planarize |
| MustNotHaveDangles | topology.AddRuleToTopology | editor.Extend + Snap |
| MustNotOverlapWith(图层间) | analysis.Intersect(A,B) | 人工判定 |
| MustBeCoveredBy | analysis.SymDiff(A, Clip(A,B)) | 调整边界 |
