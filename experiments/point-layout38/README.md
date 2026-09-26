# 38版试验：接筋端点排布与支撑指引

2026-09-26。用户授权根据图一/图二调整点筋，并按图三/图四改善楼层梁、楼层板指引。38在37的独立副本上实现；36继续是指定回退锚点，37目录、33稳定主项目及冻结快照不变。38尚未验收为新锚点。

## 加载与使用

1. 保存图纸并重启AutoCAD 2020，NETLOAD `artifacts/Debug/AutoCADPlugin-1.0.0.38.dll`，执行 `SD_VERSION` 核对38。
2. 点筋：删除本大样旧点筋，保留已有36/37纵筋、完整四倍轮廓和支撑，执行 `SD_POINT_REBAR`。
3. 支撑指引：新样式在 `SD_AUTO_DETAIL` 生成轮廓时生效。原图上的旧指引不会被自动替换；在建筑图副本选择原范围、指定新的空白插入位置生成即可。
4. 不要混用新旧点筋。重复执行相同布局不会重复添点；冲突区间仍会提示具体位置。

## 本轮点筋规则

用户已确认两条补充：

- 纵筋真实端部实际接到有限的横向/斜向纵筋时，沿该排内侧求与承接筋相切的位置作为收尾，不再按自由端退400。只有延长线相交、远处穿越或不相接的近邻，不能触发此规则。自身路径的非相邻段也可形成实际接触。
- 横排优先均布。竖排接到横排时，用相切位置计算竖排间距；交接位置由完整横排统一布置，不再强制附加竖排尾点，保留竖排中间点。如果没有有效横排覆盖交接位置，会尝试恢复尾点，冲突时明确提示。

真实转折角点及倒U顶角不移动；内部T/X仍不增加固定角点。真实自由端和支撑边缘仍执行圆心400；中间间距≤800、等分区间数最少。点筋图形标准、所属路径相切、实际打叉禁入及37的跨路径图示重叠规则不变。纵筋生成、锚固长度均未改。

[图一/图二形状的回归预览](tests/contact38-preview.png)：左侧两列中间点对齐，水平上排到左侧接触位置，底部交接不额外挤出点。共12点。该图由相似尺寸的合成DWG生成，不是从截图推定精确尺寸；预览按CAD实体坐标绘制，非CAD截图。

## 建筑轮廓阶段的指引

- 楼层梁优先使用侧面水平引线，文字置于水平线之上。
- 楼层板优先使用竖直引线，文字置于线旁。
- 直线位置被支撑、轮廓线或其他文字/引线占用时，尝试斜段＋水平文字底线，以及另一侧、上方或下方位置。
- 指向点在支撑内部，不再统一从右上角斜接到文字中部。文字高度250、图层S-TEXT，线为S-OTHER-THIN白色，仍为原生DBText和Line。
- 建筑提取、非混凝土剔除、4倍转换、支撑梁板分类、打叉和Contour/Support角色未改。文字以保守包围框避让；特别拥挤时仍会提示，不能保证任意图形都自动排得下。

[实际建筑DWG生成的轮廓及指引预览](tests/outline38-preview.png)。新指引不带Contour/Support角色，不会作为纵筋的轮廓输入。

## 修改定位

| 内容 | 源码 |
| --- | --- |
| 有限端部接筋及相切退距 | src/AutoCADPlugin/PointBarContact.cs |
| 点排收尾、竖排交接由横排接管、失败提示 | src/AutoCADPlugin/DistributedPointRebar.cs |
| 指引候选、空间避让与样式选择 | src/AutoCADPlugin/SupportLabelLayout.cs |
| 原生线和文字输出 | src/AutoCADPlugin/DetailWriter.cs |
| 建筑轮廓向指引排版传入本次轮廓线 | src/AutoCADPlugin/Commands.cs，AutoDetailCore |
| 新规则与合成图验证 | tests/PointLayout38Checks.cs |
| 原建筑DWG完整命令验证 | tests/Outline38Checks.cs、outline-command.scr |

## 验证

AutoCAD2020 / .NET Framework4.7.2编译0警告、0错误；190项几何与回归检查通过。11份DWG运行SD_POINT_REBAR，点数26/26/7/6/1/0/7/10/2/20/12，重复执行数量一致，原纵筋实体不变；11份输出原生实体检查通过，含图层、外径100、相切、完整点圆不进打叉区及点间不重叠。

另用原建筑DWG的后台副本执行SD_AUTO_DETAIL，验证两处给定SD-REGION、比例4、轮廓和支撑角色/图层、文字分类/高度及不生成钢筋。参考DWG未覆盖。发布DLL与上述测试DLL哈希一致；7个冻结锚点的清单校验全部通过。

记录见 [validation38.json](tests/validation38.json)、`tests/offset-results.txt`、`tests/native-commands38.json`、`tests/native-validation38.txt`、`tests/outline-command38-validation.txt`。正式点筋输出在 `tests/final-run/`。

相似形状样例仍有两处轮廓裁断无合法端点的提示，见 `tests/contact38-diagnostics.txt`；不把这些区间声称为已补齐。其他既有的短段400、手工改形锚固身份匹配、只支持直线XY纵筋等限制继续保留。

## 回退

先保留38代码与测试图副本，再保存并重启CAD，NETLOAD `../../anchors/1.0.0.36/AutoCADPlugin-1.0.0.36.dll`。清理38点筋后按36重画。切换DLL不会自动撤销图内实体。需要继续实验时复制完整36源码到新目录，不覆盖现有试验和冻结锚点。
