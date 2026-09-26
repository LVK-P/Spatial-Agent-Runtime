# Agent 文件管理规范

## 原则

```
结果给用户看     → 添加到地图，在 _output.gdb
过程自己留着     → 不添加到地图，在 _process.gdb
命名要能看懂     → 用户可以在目录面板找到并手动打开
```

## 目录结构

每次打开 ArcGIS Pro 项目时，Agent 自动在项目文件夹下创建：

```
{项目文件夹}/
└── agent_workspace/
    ├── _process.gdb     ← 中间步骤的临时数据
    └── _output.gdb      ← 最终结果，添加到地图
```

如果项目文件夹无法确定（比如新建空项目），则在 `Documents\ArcGIS\AgentWorkspace\` 下创建。

## 命名规范

### 过程文件（_process.gdb 内）
格式：`{操作类型}_{简要描述}_{序号}`

| 操作类型 | 示例 |
|---------|------|
| project | `project_三调图斑_to_4543` |
| buffer | `buffer_道路_50m` |
| intersect | `intersect_用地_坡度` |
| dissolve | `dissolve_图斑_按YSDM` |
| topology | `topo_check_三调图斑` |
| fieldcalc | `fieldcalc_面积_公顷` |
| select | `select_坡度_gt25` |
| export | `export_统计表_temp` |

### 结果文件（_output.gdb 内）
格式：`{用户意图的简短描述}`

| 用户说的 | 结果命名 |
|---------|---------|
| "把坡度大于 25°的地块标记出来" | `坡度大于25度地块` |
| "统计每个村的建设用地面积" | `各村建设用地面积统计` |
| "把三调图斑转成用地用海分类" | `用地用海转换结果` |

## Agent 行为规则

### 开始任务时
1. 确认项目文件夹路径
2. 检查 agent_workspace 是否存在 → 不存在则创建
3. 确认 _process.gdb 和 _output.gdb 存在 → 不存在则创建

### 执行过程中
1. 每个中间步骤的产出 → 写入 _process.gdb
2. 命名遵循上述规范
3. 不主动添加到地图

### 任务完成时
1. 最终结果 → 写入 _output.gdb
2. 自动添加到当前地图
3. 缩放到结果范围
4. 向用户报告：
   - ✅ 做了什么
   - 📁 结果在哪里（图层名 + gdb 路径）
   - 🔧 过程文件在哪里（可选项，用户需要时才说）

### 用户询问过程文件时
1. 告诉用户 _process.gdb 的完整路径
2. 告诉用户里面的要素类名称
3. 用户自行在目录面板中添加

### 清理策略
- 每次新会话开始时，提示用户是否清理上次的 _process.gdb
- _output.gdb 不自动清理（由用户决定）
