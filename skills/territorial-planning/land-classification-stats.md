# 用地分类面积统计

## 触发条件
用地分类, 面积统计, 地类汇总, 土地面积, 分类面积, 地类占比, 用地结构

## 执行流程

### Step 1：确认图层和字段
调用 `/layers` 列出所有图层，确认目标图层名。
调用 `/layer <图层名>` 查看图层字段，找到用地类型字段（DLBM/DLMC/地类编码/地类名称等）。

目标：锁定图层名 + 用地类型字段名。

### Step 2：添加面积字段
```
/run management.AddField <图层名> Area_km2 DOUBLE
```
检查项：字段是否添加成功

### Step 3：计算椭球面积
```
/run management.CalculateField <图层名> Area_km2 '!shape.geodesicArea@squarekilometers!' PYTHON3
```
检查项：面积字段是否计算出值（非0），范围是否合理。

### Step 4：按地类汇总
```
/run analysis.Statistics <图层名> <输出GDB路径> "Area_km2 SUM" <用地类型字段>
```
检查项：输出表记录数是否与用地类型数一致。

### Step 5：计算占比（可选）
```
/run management.AddField <汇总表> Ratio DOUBLE
/run management.CalculateField <汇总表> Ratio '!SUM_Area_km2! / <总面积> * 100' PYTHON3
```

## 输出确认清单
- [ ] 面积字段已添加且非空
- [ ] 汇总表记录数 = 用地类型数量
- [ ] 面积合计 ≈ 图层总面积（误差 < 1%）
- [ ] 占比合计 ≈ 100%（误差 < 1%）

## 常见坑
1. 字段名含中文 → 引号包裹
2. 输出路径含空格 → 使用 GDB 路径而非文件夹路径
3. 坐标系不是投影坐标系 → 使用 `!shape.geodesicArea@squarekilometers!` 而非 `!shape.area!`
4. 汇总字段名自动添加前缀如 `SUM_Area_km2` — 检查实际输出字段名
