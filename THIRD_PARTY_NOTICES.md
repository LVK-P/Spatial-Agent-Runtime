# 第三方声明与许可边界

## MIT License 的适用范围

仓库根目录的 [MIT License](LICENSE) 仅授权 Spatial Agent Runtime 贡献者原创并有权许可的源码与文档。该许可不授予任何第三方软件、SDK、商标、服务标识、文档、数据或本地生成内容的权利。

如果某个文件另有许可声明，应以该文件的声明为准。使用者仍需自行判断其具体使用、部署和分发方式是否满足所在地区法律、第三方协议及组织合规要求。

## Esri 与 ArcGIS 依赖

本项目是独立开发的互操作性演示，当前可连接 ArcGIS Pro，但与 Esri 不存在隶属、合作、赞助、认证或背书关系。

- ArcGIS、ArcGIS Pro、Esri 及相关名称和标识的权利归其各自权利人所有。
- 本仓库不分发 ArcGIS Pro、ArcPy、ArcGIS Pro SDK、Esri 程序集、安装包或许可证文件。
- 构建和运行完整 Add-In 需要使用者自行取得并遵守适用的 ArcGIS Pro 与 SDK 许可。
- 源码中的产品名称、公开 API 类型和调用标识仅用于说明兼容性及实现互操作，不表示所有权或授权关系。

适用条款应以权利人发布的最新版本为准：

- [Esri Master Agreement](https://www.esri.com/en-us/legal/terms/master-agreement-product)
- [Product-Specific Terms of Use](https://www.esri.com/en-us/legal/terms/product-specific-scope-of-use)
- [Esri Product Naming Guide](https://www.esri.com/content/dam/esrisites/en-us/media/legal/copyrights-and-trademarks/esri-product-naming-guide.pdf)

## 本地生成的互操作元数据

公开发行不包含从 ArcGIS 安装环境批量生成的工具 manifest、检索索引、风险目录或工具帮助文档。使用者可在自己已获许可的环境中运行 `tools/generate_local_arcgis_metadata.py`，生成仅供本机使用的接口元数据。

这些生成文件：

- 默认写入被 Git 忽略的 `local-assets/`；
- 不属于本项目公开发行内容；
- 不因与本项目一起构建或运行而自动获得 MIT 授权；
- 不应在缺乏独立授权依据时提交、发布或再分发。

生成脚本刻意不读取或复制帮助正文、工具说明、参数默认值和文档页面，只保留 Runtime 调用所需的接口结构。即便如此，使用者仍应依据其许可协议决定是否生成、保存和使用这些文件。

## 免责声明

本文件用于说明项目的发行设计与许可边界，不构成法律意见，也不能保证任何特定使用方式合规。如计划进行商业部署、再分发或品牌宣传，请向具备相应资质的法律专业人士核实。
