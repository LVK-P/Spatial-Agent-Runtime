# 本地互操作资产

此目录用于存放由使用者在自己已获许可的 ArcGIS Pro 环境中生成的运行时元数据。除本说明和空示例外，目录中的 JSON 文件均被 Git 忽略，不属于本仓库发布内容，也不由本项目的 MIT License 授权。

请在 ArcGIS Pro Python Command Prompt 中运行：

```powershell
python tools\generate_local_arcgis_metadata.py
```

脚本只记录调用接口所需的结构事实，不复制帮助正文、工具说明或默认路径。生成的动态工具默认按 `Destructive` 处理，只有在使用者自行审查并创建 `tool_risk_catalog.json` 后，才应降低风险级别。

构建 Add-In 时会自动包含当前目录中存在的 `tools_manifest.json`、`tools_index.json` 和可选的 `tool_risk_catalog.json`。也可通过环境变量 `SPATIAL_AGENT_LOCAL_ASSETS_DIR` 指向仓库外目录。
