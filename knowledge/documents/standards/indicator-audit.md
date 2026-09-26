# 国土空间规划 — 常用指标核算

## 约束性指标

| 指标 | 计算方式 | GP 工具 |
|------|---------|--------|
| 耕地保有量 | 01类面积合计 | Statistics (SUM) |
| 永久基本农田面积 | 基本农田图层面积合计 | Statistics (SUM) |
| 建设用地总规模 | 07-13类面积合计 | Statistics (SUM) |
| 城乡建设用地规模 | 城镇+村庄建设用地面积 | SelectByAttribute + Statistics |
| 人均城镇建设用地 | 城镇建设用地 / 城镇人口 | CalculateField |

## 预期性指标

| 指标 | 计算方式 | GP 工具 |
|------|---------|--------|
| 森林覆盖率 | (03类面积 / 总面积) * 100% | CalculateField |
| 草原综合植被盖度 | 04类面积分析 | Statistics |
| 湿地保护率 | 受保护湿地 / 湿地总面积 | Intersect + Statistics |

## 用地结构变化

计算两期数据之间的地类变化矩阵：
1. `analysis.Intersect` 将两期用地图层叠加
2. 添加字段标记变化类型（转出/转入/不变）
3. `analysis.Statistics` 按变化类型汇总面积

## GP 表达式速查

面积计算（平方公里）：
```
/run management.AddField layer_name Area_km2 DOUBLE
/run management.CalculateField layer_name Area_km2 '!shape.area@squarekilometers!' PYTHON3
```

用地分类汇总：
```
/run analysis.Statistics layer_name output_table "Area_km2 SUM" land_type
```

分类面积占比：
```
/run management.CalculateField layer_name Ratio '!Area_km2! / total_area * 100' PYTHON3
```
