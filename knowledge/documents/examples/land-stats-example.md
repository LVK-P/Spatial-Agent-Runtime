# 案例模板：用地分类面积统计

## 场景描述

用户有一个国土调查图层，需要按用地类型统计面积，并计算各地类占比。

## 操作流程

### Step 1：确认字段
先查询图层包含哪些字段，确认有用地类型字段（如 DLBM、DLMC、地类编码等）。

### Step 2：添加面积字段
```
/run management.AddField <图层名> Area_km2 DOUBLE
```

### Step 3：计算椭球面积（推荐）
```
/run management.CalculateField <图层名> Area_km2 '!shape.geodesicArea@squarekilometers!' PYTHON3
```

### Step 4：按地类汇总面积
```
/run analysis.Statistics <图层名> output_stats "Area_km2 SUM" <用地类型字段>
```

### Step 5：计算占比
在汇总表中计算各地类面积占总面积的百分比。

## 注意要点
- 地理坐标系或跨较大区域统计时，优先使用 `!shape.geodesicArea@squarekilometers!` 计算测地线面积
- 汇总输出建议使用 GDB 表而非 shapefile
- 如果字段名含中文，用引号包裹
