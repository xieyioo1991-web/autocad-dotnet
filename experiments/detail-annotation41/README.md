# 41 尺寸标注与图名试验草稿

从40源码独立复制。规则及状态见[阶段文档](../../docs/尺寸标注与图名阶段.md)。不是发布版本或锚点。

目前只有样式导入、图名整组复制及关联辅助代码，尚未接入CAD生成命令。图名内容为原样檐口大样图、圈内1及下方分布点筋说明。预留1800属于首版排版尝试值，后续可按图面调整。

原生验证：`tests/title-checks.txt`，9项通过；测试使用独立DWG副本，在AutoCAD2020后台引擎内验证真实实体，最后不提交事务。包含原生跨库导入的失败回滚，不只验证编译。

生产插件暂不发布。临时编译使用`dotnet build src/AutoCADPlugin/AutoCADPlugin.csproj -p:DesignTimeBuild=true`，避免生成尚未完成的版本化发布DLL。后续发布需要保留DLL旁Fonts目录及ReferenceStyle.dwg副本。
