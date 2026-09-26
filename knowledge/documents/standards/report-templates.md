# 规划报表模板指南

## 触发条件
用地结构表, 用地汇总, 面积占比, 规划调整表, 现状规划对比, 调入调出, 三大类汇总, 建设用地平衡, 人均建设用地, 占补平衡, 指标核算, 规划指标, 约束性指标, 生成报表, 导出Excel, 用地统计

## 六种标准报表

### 报表1：用地结构表
```
用途: 按一级类/二级类汇总各用地类型的面积和占比
分组字段: YDLX (用地类型代码)
面积字段: JSMJ (计算面积, 平方米)
列: 用地代码 | 用地名称 | 面积(公顷) | 占全域比例(%) | 占建设用地比例(%) | 人均面积(㎡/人)

GP工具链:
1. management.AddField 添加面积字段
2. management.CalculateField '!shape.area@squarekilometers!' 计算面积
3. analysis.Statistics 按YDLX分组 SUM面积
4. management.CalculateField 计算占比 = SUM_Area / 总面积 * 100
5. 可选: management.AddJoin 连接LandClassificationData获取用地名称
```

### 报表2：规划用地调整表
```
用途: 对比基期年现状用地和规划目标年用地，计算各地类增减量
分组字段: YDLX
列: 用地代码 | 用地名称 | 现状面积(公顷) | 现状占比(%) | 规划面积(公顷) | 规划占比(%) | 增减量(公顷) | 增减幅度(%)

GP工具链:
1. 分别对现状图层和规划图层执行 Statistics 按YDLX汇总
2. management.AddJoin 将两个汇总表按YDLX关联
3. management.CalculateField 计算: 增减量 = 规划面积 - 现状面积
4. management.CalculateField 计算: 增减幅度 = 增减量/现状面积*100
5. 筛选增减量!=0 的行 → "用地调整清单"
```

### 报表3：三大类面积汇总表
```
用途: 按《土地管理法》农用地/建设用地/未利用地三大类汇总
分组字段: 三大类 (需先用LandClassificationData.GetSdCategory()分类)
列: 大类名称 | 面积(公顷) | 占比(%) | 图斑数

分类依据:
- 农用地: DLBM IN (LandClassificationData.SdCategorySql["NYDMJ"] 去除引号后的代码列表)
- 建设用地: DLBM IN (LandClassificationData.SdCategorySql["JSYDMJ"] 的代码列表)
- 未利用地: 不在上述两个集合中的地类

GP工具链:
1. management.AddField 添加"三大类" TEXT字段
2. management.SelectLayerByAttribute + CalculateField 分别赋值三大类
3. analysis.Statistics 按"三大类"分组 SUM面积 + COUNT
4. management.CalculateField 计算占比
```

### 报表4：建设用地平衡表
```
用途: 详细列出各类建设用地面积、占比和人均指标
分组字段: YDLX (仅建设用地一级类: 07/08/09/10/11/12/13/14/15/16+HH)
列: 用地代码 | 用地名称 | 面积(公顷) | 占建设用地(%) | 占全域(%) | 人均面积(㎡/人) | 国标下限 | 达标判断

关键指标(参照GB 50137):
- 居住用地(07): 人均23-38㎡
- 公共管理与公共服务用地(08): 人均≥5.5㎡
- 绿地与开敞空间用地(14): 人均≥10㎡, 其中公园绿地≥8㎡
- 道路与交通设施(12): 占建设用地10-25%

GP工具链:
1. SelectLayerByAttribute WHERE 一级类代码 IN 建设用地列表
2. analysis.Statistics 按YDLX分组
3. management.CalculateField 计算人均 = 面积/规划人口
4. 对比国标下限 → IF 人均>=下限 THEN '达标' ELSE '不达标'
```

### 报表5：耕地占补平衡表
```
用途: 跟踪规划期内耕地占用(转建设用地)和补充(开发整理复垦)
分组字段: 变更类型(占用/补充)
列: 变更类型 | 涉及耕地面积(公顷) | 补充耕地面积(公顷) | 净增减(公顷) | 占补平衡判断

GP工具链:
1. analysis.Intersect(规划建设用地_新增, 现状耕地) → 占用耕地图斑
2. analysis.Erase(现状耕地, 规划建设用地) → 保留耕地
3. SelectLayerByAttribute WHERE 地类=后备耕地 AND 规划=耕地 → 补充耕地
4. 分别 Statistics SUM 占用面积和补充面积
5. 净增减 = 补充 - 占用
6. IF 补充>=占用 THEN '平衡' ELSE '缺口: 需补充X公顷'
```

### 报表6：用地指标核算表
```
用途: 核算耕地保有量、林地保有量、建设用地总规模等关键规划指标
列: 指标名称 | 规划目标值 | 现状值 | 差值 | 达标判断 | 指标属性

常见指标:
- 耕地保有量(公顷): 约束性, 现状>=目标才算达标
- 永久基本农田保护面积(公顷): 约束性, 现状>=目标
- 林地保有量(公顷): 预期性
- 建设用地总规模(公顷): 约束性, 现状<=目标才算达标
- 城乡建设用地规模(公顷): 约束性, 现状<=目标
- 人均城镇建设用地(㎡/人): 约束性
- 人均农村居民点用地(㎡/人): 预期性
- 生态红线面积(公顷): 约束性, 现状>=目标
- 湿地面积(公顷): 预期性

GP工具链:
1. analysis.Statistics 分别对耕地/林地/建设用地/生态红线图层 SUM面积
2. 耕地: DLBM IN ('0101','0102','0103')
3. 永久基本农田: 直接SUM永久基本农田图层面积
4. 建设用地总面积 = SUM(建设用地图层面积)
5. 城乡建设用地 = SUM(城镇建设用地) + SUM(村庄建设用地)
6. 人均城镇建设 = 城镇建设用地 / 城镇人口
7. 逐项对比 现状值 vs 规划目标值 → 判断达标
```

## 生成Excel的注意事项

```
1. 输出格式: 先用analysis.Statistics生成GDB表，再用management.TableToExcel导出
2. 面积单位: 默认平方米，报表中除以10000转公顷，或除以1000000转平方公里
3. 小数位: 面积保留2位小数，占比保留2位小数
4. 坐标系统: 必须使用CGCS2000投影坐标系，否则!shape.area!不准确
5. 分层统计: 如果数据跨行政区，先按XZQDM分组，再按YDLX分组
6. 零值处理: 调整表中增减量为0的行是否需要显示 → 默认显示
```
