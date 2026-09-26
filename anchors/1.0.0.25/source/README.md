# 试验25：支撑接口处纵筋完全断开

当前固定锚点为用户已确认的1.0.0.23。根目录src/AutoCADPlugin和artifacts/Debug保留23版，完整锚点在anchors/1.0.0.23，21版历史锚点保留。此目录为25版独立试验，测试通过不代表成为新锚点。

## 使用

1. 保存图纸并重启AutoCAD 2020。
2. NETLOAD本目录artifacts/Debug/AutoCADPlugin-1.0.0.25.dll，用SD_VERSION确认25版。
3. 已有23版钢筋时，先删除本次大样中的旧洋红纵筋，保留白色轮廓、支撑框、叉号及文字。插件遇到原闭合纵筋会提示，避免叠加。
4. SD_REBAR框选已放大四倍的完整结构轮廓（包括支撑），回车生成。没有轮廓时，先执行SD_AUTO_DETAIL。
5. 不符合预期时回退23：撤销/删除25版生成的钢筋，保存并重启CAD，加载根目录artifacts/Debug/AutoCADPlugin-1.0.0.23.dll。切换DLL不会自动修改已有图形。

## 本轮规则

- 沿用23版的轮廓识别、支撑扣除、共边合并和角点接触分区，然后向区域内偏移50。
- 找出配筋边界与支撑多段线具有实际长度的共用边，删除其对应的偏移纵筋封口段。支撑叉号仅用于显示；判断依据是支撑边界，不是叉号或文字位置。
- 完整接口去掉整段封口，局部接触只断开对应部分；同一区域多处接触可拆成多条开放纵筋。
- 开口输出为Closed=false的真实开放多段线，不用白线遮盖，不保留首尾隐藏连接。
- 仅角点接触、附近但不相接的支撑不会触发断开；没有支撑接口的纵筋仍保持闭合。
- 其他钢筋保持原位置，接口处不另外延伸、搭接或加锚固。白色轮廓、支撑框、叉号、梁板文字不修改。
- 纵筋S-REIN、ACI6洋红、ConstantWidth=35、连续线型；颜色/线型/线宽随层。50为已放大四倍后的中心线偏移，不再乘4。本轮不生成点筋。

## 轮廓输入及限制

SD_AUTO_DETAIL使用XData区分Contour和Support，从而排除叉号及引线。兼容21版输出约定：没有角色标记时，S-OTHER-THIN图层的闭合多段线视为支撑。不要将任意闭合配筋轮廓放在此图层冒充旧版支撑；有歧义时用当前版本重新生成轮廓。

仅支持Z=0直线轮廓。过窄导致50偏移坍缩、圆弧、内孔/嵌套边界、不能分解的退化环仍会失败并回滚。选区缺少支撑边界时无法识别对应接口，需框选完整大样。

## 验证及检查文件

AutoCAD 2020 accoreconsole原生运行25项测试通过。保留原有偏移几何/点接触测试，并增加无支撑、单端支撑、两端支撑、局部接触、同边多处接触、点接触不误断、旋转/镜像/大坐标、斜接口及屋面/檐口示意案例。检查端点、保留长度、开放标志、宽度、颜色、旧23版钢筋冲突及重复生成拦截。

- tests/offset-results.txt：全部几何和实体测试结果。
- tests/support-opening-example.dwg：按用户图片连接关系构造的测试图，已生成两条开放纵筋，并非用户截图对应的实际DWG。
- tests/support-opening-preview.png：根据生成DWG的实体坐标绘制，非CAD截图，省略文字。
- tests/support-opening-example-command-results.txt：加载正式25版DLL，实际SD_REBAR新增2条纵筋，闭合数0；重复执行仍为2条。
- tests/point-contact-tagged-command-results.txt、tests/point-contact-legacy-command-results.txt：带角色标记与旧版格式均新增2条开放纵筋，重复执行不叠加。
- tests/offset50-sample-command-results.txt：原始建筑实例加梁板支撑新增1条开放纵筋，重复执行不叠加。

## 实现及构建

SupportOpening.cs负责共用接口识别、偏移封口对应及开放路径拆分；RebarPath.cs显式保存顶点和是否闭合；OffsetRebar.cs沿用原偏移校验并写入对应多段线，检测旧23版闭合钢筋及新版重复路径。

编译：dotnet build experiments/offset50/src/AutoCADPlugin/AutoCADPlugin.csproj

试验产物在本目录artifacts，根目录artifacts/Debug仅保存已接受的23版。23版源码与测试快照已封存于../../anchors/1.0.0.23，22版历史源码和复现日志在history/1.0.0.22。

## 25版修复记录

24版的共边判定使用固定1e-8角度阈值，比轮廓拼接的0.1图形单位容差严格得多。已复现：轮廓一个顶点仅偏移0.001时，支撑侧封口仍保持闭合。25版改为按短边上的横向偏差及共用区间两端距离判断，统一使用0.1图形单位容差；没有放大吸附范围。

新增测试覆盖0.001误差、六组旋转/镜像/大坐标、间隔0.2的非接触支撑、原建筑实例的梁顶齐檐口场景。25项AutoCAD 2020原生测试通过。正式25版DLL运行两个接口案例均新增2条开放纵筋，闭合数0，重复执行无新增。

- history/1.0.0.24/source-1.0.0.24.zip：修复前源码与测试快照。
- history/1.0.0.24/reproduced-before25.txt：0.001坐标偏差导致封口未断的失败记录。
- tests/precision-interface.dwg：原建筑实例提取轮廓，将梁顶调整为齐檐口后的两接口测试结果。
- tests/precision-interface-command25-results.txt、tests/support-opening-example-command25-results.txt：正式命令验证。
- tests/precision-interface-preview.png：实体坐标检查图，非AutoCAD截图。

用户最新截图对应的已修改DWG尚未收到；上述复现证明了可导致同类现象的数值判定缺陷，不代表已确认截图中的具体坐标就是同一原因。23版锚点、主项目和根目录发布文件均未修改。