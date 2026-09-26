# 34版：阳角点筋试验

从33版冻结源码复制，稳定目录及anchors不变。当前命令是SD_POINT_REBAR；SD_AUTO_DETAIL、SD_REBAR沿用33版行为。完整规则、边界、修改定位及测试说明见项目根目录 `docs/点筋布置阶段.md`。

保存并重启AutoCAD，NETLOAD `artifacts/Debug/AutoCADPlugin-1.0.0.34.dll`，执行SD_VERSION核对。已有纵筋不必重画；SD_POINT_REBAR框选完整四倍轮廓、支撑和纵筋后回车。命令只新增阳角点筋。

样式沿用目标DWG：S-REIN-POINT、颜色30、实心圆实际外径100（中心线直径50、宽50的圆弧多段线）。与宽35纵筋的内侧边相切，圆心距中心线67.5。实际打叉支撑禁止点筋；未打叉的梁顶延伸区域允许点筋；倒U顶部两角允许压到其他锚固筋，仍保持与U两边相切。普通候选若越界、空间不足或碰其他纵筋则跳过并反馈数量。本轮不布置沿边分布点筋。

验证：140项后台检查通过；6个最终命令样例点筋数为9/10/5/2/1/0，纵筋未改，重复运行不叠加。测试数据在tests，最终输出使用 `*-points34-final.dwg`。`points-preview34.png`为实体坐标预览，非CAD截图。短竖段不足相切长度时不强行放点。

构建：`dotnet build experiments/point-corners/src/AutoCADPlugin/AutoCADPlugin.csproj`（从项目根目录执行）。遵循版本化发布；本版尚未获用户验收，不作为新锚点。失败时回退33；切换DLL不会自动删除已生成点筋。
