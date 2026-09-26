# 双评价（资源环境承载能力和国土空间开发适宜性评价）

## 触发条件
双评价, 承载力评价, 适宜性评价, 资源环境, 生态敏感性, 土地资源评价, 水资源评价

## 执行流程

### Step 1：准备数据
确认有以下数据图层：
- DEM（数字高程模型）
- 土地利用现状
- 土壤类型
- 降水量分布
- 地质灾害风险区

### Step 2：土地资源评价（坡度分级）
```
/run management.AddField <DEM图层> Slope DOUBLE
/run 3d.Slope <DEM图层> Slope PERCENT_RISE
/run management.AddField <DEM图层> SlopeGrade TEXT
/run management.CalculateField <DEM图层> SlopeGrade '"高"' PYTHON3
```
坡度分级标准：
- ≤8°：平缓（高适宜）
- 8-15°：较缓（中适宜）
- 15-25°：较陡（低适宜）
- >25°：陡峭（不适宜）

### Step 3：生态敏感性叠加
```
/run analysis.Union <坡度分级> <生态红线图层> <敏感度结果>
```

### Step 4：城镇建设适宜性综合评价
叠加坡度、地质灾害、基本农田、生态红线等因子，计算综合适宜性等级。

## 输出确认清单
- [ ] 坡度已正确计算（值域合理）
- [ ] 分级标准正确应用
- [ ] 叠加分析无遗漏图层
- [ ] 适宜性等级分布合理

## 常见坑
1. DEM 分辨率不一致 → 重采样到统一分辨率
2. 坡度单位混淆（度 vs 百分比）→ 确认使用 PERCENT_RISE 还是 DEGREE
3. 矢量/栅格混合 → 统一转为栅格做叠加分析
