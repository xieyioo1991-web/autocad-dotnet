# 试验23：配筋轮廓向内偏移50，支持角点接触的独立区域

当前正式锚点仍为1.0.0.21。此目录是独立试验副本，不是新锚点。

## 23版修正

22版合并共边后，要求边界每个交点只剩一条可走的出边。扣除支撑后，两个配筋区仅在角点接触时会留下两条出边，因此报“点接触位置存在分叉”；这并不等于用户轮廓未闭合。

23版按交点处线段的方向顺序追踪独立闭合边界，角点接触的区域分别向内偏移50，不跨支撑强行连成一圈。嵌套检查改用区域内部的点，避免把共用角点误判为内孔。偏移距离、纵筋属性和支撑扣除规则不变。

22版源码快照及修复前复现日志保存在history/1.0.0.22；22版DLL由发布脚本归档到artifacts/Debug/历史版本/1.0.0。

## 使用

1. 保存图纸并重启AutoCAD 2020。
2. NETLOAD此目录的artifacts/Debug/AutoCADPlugin-1.0.0.23.dll，用SD_VERSION确认23版。
3. 用SD_AUTO_DETAIL生成四倍结构轮廓，然后执行SD_REBAR，框选完整生成结果并回车。
4. 支撑区域由SD-REGION确定：保留轮廓、叉号、梁/板名称，但从配筋区域扣除。
5. SD_REBAR只生成闭合纵筋多段线：向内偏移50，S-REIN，ACI6洋红，ConstantWidth=35，连续线型。

50是已经放大四倍后的图形距离，不再乘4；是钢筋中心线距离，35宽多段线在直线段处的可见外缘距原轮廓32.5。本次不生成点筋，不附加锚固、搭接、钢筋文字。

## 轮廓逻辑

- 试验版SD_AUTO_DETAIL给轮廓和支撑附加XData角色，不改变已有图形的外观。SD_REBAR借此排除引线和叉号。
- 支持21版生成的线段轮廓：S-OTHER-THIN上的闭合多段线按21版输出约定视为支撑，排除支撑对角线，悬空引线不形成闭合区域。不要把任意闭合配筋轮廓放在此图层冒充21版支撑；有歧义时使用试验版重新生成轮廓。
- 将直线交点和共线重叠端点拆分，建立平面闭合区域，去除支撑区域，合并相邻配筋区域的共边，再取外边界。
- 共边相邻的区域合并后偏移；仅角点接触的区域各自闭合后偏移。
- 通过AutoCAD GetOffsetCurves得到候选偏移线，以区域包含关系、面积和到原边界的距离判断内侧，不依赖轮廓顶点顺序。
- 不使用Example1RebarData固定坐标；原模板类保留在副本中作历史参考，但SD_REBAR不调用它。

当前只支持Z=0的直线平面轮廓。不能形成闭合区域、过窄导致50偏移坍缩、圆弧、内孔/嵌套边界及无法分解的退化环会提示失败，不提交部分输出。点筋和支撑收头等待后续规则。选区中缺少支撑边界时无法扣除该支撑，务必选择完整大样。

AutoCAD偏移API参考：https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Curve_GetOffsetCurves_double.html

## 验证

已在AutoCAD 2020 accoreconsole运行14项测试：原有11项通过，新增角点接触矩形、截图相同连接关系的挑出部分/梁顶小区域、带叉号及文字引线的新旧标记图形。两组角点案例分别检查8种起点/顺序/旋转及大坐标组合，生成的两条钢筋面积符合独立计算值。

截图案例是按连接关系构造的测试图，并非读取用户截图所对应的实际DWG。

- tests/offset-results.txt：几何及实体测试结果。
- tests/offset50-sample-command-results.txt：23版实际SD_REBAR在原实例新增1条纵筋，重复执行仍为1条。
- tests/point-contact-tagged-command-results.txt、tests/point-contact-legacy-command-results.txt：分别加载带角色标记及兼容21版约定的测试图，实际SD_REBAR新增2条纵筋，重复执行仍为2条。
- tests/point-contact-tagged.dwg、tests/point-contact-legacy.dwg：按截图连接关系构造的两区域结果，可打开检查。
- tests/offset50-sample.dwg：后台生成的示例，可直接打开检查。
- tests/preview.png：从生成实体坐标绘制的检查图，非AutoCAD截图，省略文字。

编译：dotnet build experiments/offset50/src/AutoCADPlugin/AutoCADPlugin.csproj
试验产物位于本目录artifacts，不影响根目录的21版产物。回退请保存图纸、重启AutoCAD并加载../../anchors/1.0.0.21/中的DLL；源码主目录仍是21版，无需替换。
