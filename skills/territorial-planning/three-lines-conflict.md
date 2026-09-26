# 三区三线冲突检测

## 触发条件
三区三线, 冲突检测, 生态红线, 永久基本农田, 城镇开发边界, 占用检测, 压覆分析, 合规性检查

## 执行流程

### Step 1：确认图层
- 规划用地图层
- 生态保护红线图层
- 永久基本农田图层
- 城镇开发边界图层
确认各图层的坐标系一致（推荐 CGCS2000）。

### Step 2：检测生态红线冲突
```
/run analysis.Intersect <规划图层> <生态红线图层> conflict_eco
```
检查项：输出图层要素数。若为 0 则无冲突。

### Step 3：检测基本农田冲突
```
/run analysis.Intersect <规划图层> <基本农田图层> conflict_farm
```
检查项：输出图层要素数。

### Step 4：检测城镇开发边界越界
```
/run analysis.Clip <建设用地图层> <开发边界图层> inside_boundary
/run analysis.SymDiff inside_boundary <建设用地图层> outside_boundary
```
检查项：outside_boundary 要素数。

### Step 5：统计冲突面积
```
/run management.AddField conflict_eco ConflictArea DOUBLE
/run management.CalculateField conflict_eco ConflictArea '!shape.area@squarekilometers!' PYTHON3
/run analysis.Statistics conflict_eco conflict_stats "ConflictArea SUM"
```

## 输出确认清单
- [ ] 所有冲突图层坐标系一致
- [ ] 冲突面积 > 0 时已标记到地图
- [ ] 冲突图斑数 + 冲突总面积已记录
- [ ] 生成的冲突检测结果可在地图中查看

## 常见坑
1. 图层坐标系不一致 → 先投影到统一坐标系
2. 大量图斑叠加导致超时 → 先用 SelectLayerByAttribute 缩小范围
3. SymDiff 需要两个图层在同一 GDB 中
