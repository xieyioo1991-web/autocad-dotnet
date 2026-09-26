# AutoCAD 2020 plugin setup

项目已配置为 .NET Framework 4.7.2，并引用本机 AutoCAD 2020 Managed API：
`D:\Program Files\Autodesk\AutoCAD 2020`

生成：

```powershell
dotnet build .\AutoCAD二次开发.slnx
```

输出 DLL：`src\AutoCADPlugin\bin\Debug\net472\AutoCADPlugin.dll`

当前验证版（1.0.0.20）：`artifacts\Debug\AutoCADPlugin-1.0.0.20.dll`

构建成功后自动运行 `scripts\Publish-Plugin.ps1`：`artifacts\Debug`（Release 构建对应 `artifacts\Release`）外层只保留最新版本的 DLL 和 PDB，旧文件移动到 `历史版本\主版本.次版本.构建号\`。例如 `1.0.0.18` 归入 `历史版本\1.0.0`，未来的 `1.0.1.x` 归入 `历史版本\1.0.1`。

发布代码改动前递增项目的 `<Version>`；同一版本、相同内容的重复构建不会改动已发布文件。脚本不会覆盖内容不同的同版本文件或历史文件。

如果 AutoCAD 正在占用旧 DLL，请使用带版本号的新文件：`artifacts\Debug\AutoCADPlugin-1.0.0.20.dll`。

每次重新加载前先关闭并重新打开 AutoCAD 2020，然后加载以下任一已同步文件：

```text
src\AutoCADPlugin\bin\Debug\net472\AutoCADPlugin.dll
artifacts\Debug\AutoCADPlugin-1.0.0.20.dll
```

加载后执行 `SD_VERSION`，应显示版本 `1.0.0.20` 和实际加载路径。如果版本或路径不对，说明 AutoCAD 仍在使用旧程序集。

在 AutoCAD 2020 中加载：

1. 执行 `NETLOAD`。
2. 选择上述 DLL。
3. 执行命令 `HELLOCAD` 验证插件。

## 第一版结构大样命令

### 标记支撑区域

执行 `SD_SETUP` 创建标准图层，然后在建筑大样中使用 `SD-REGION` 图层绘制闭合的轻量多段线。每个闭合多段线代表楼层梁、楼层板等需要保留表达但不需要自动配筋的支撑区域，不需要分别创建梁、板、墙、柱图层。

### 轮廓识别阶段

执行 `SD_AUTO_DETAIL`，依次框选建筑大样、指定结构大样插入点。插件读取建筑大样中的 `WALL`、`COLUMN` 两个图层和用户绘制的 `SD-REGION` 闭合多段线，并根据 `PUB_HATCH` 材料边界保留混凝土、剔除非混凝土，再按 1:100 到 1:25 的 4 倍比例转换到结构大样：

- `S-OTHER-THIN` 上的控制轮廓；
- 支撑区域内的对角线叉号；
- `S-TEXT` 上的“楼层梁”文字和引线；
- `WALL`、`COLUMN` 轮廓统一映射到结构实例的 `S-OTHER-THIN` 图层，实体颜色为白色。若原图把填充斜线作为 WALL/COLUMN 上的真实线对象，程序会根据其所在的封闭填充区域剔除内部填充线，只保留填充边界。
- `SD-REGION` 闭合多段线边界按用户绘制的顶点和边逐段复制，不做裁剪、延伸、相交推断或补线；区域内另外生成对角线打叉、“楼层梁”文字和引线。
- 混凝土 Hatch 边界先按共线边和两侧区域关系筛选，只保留混凝土区域的外侧边；内部边、相邻混凝土区域共边不会重复输出。

多个 `SD-REGION` 支撑区域的“楼层梁”标识会优先放在区域上方，并依次尝试右侧、左侧和下方位置；候选位置与其他支撑区域或已有文字冲突时自动换位，使用引线连接回支撑区域边界。

`SD-REGION` 仅表示楼层梁、楼层板等支撑区域。混凝土填充边界一律保留；非混凝土填充边界及其对应的 WALL/COLUMN 外轮廓不保留。当前轮廓校准阶段不做墙柱几何推断，不生成纵筋和点筋。输出轮廓使用结构大样标准图层，并将实体颜色固定为白色。

当前阶段不生成纵筋和点筋，先验证“框选、建筑轮廓提取、支撑区域表达、比例转换和结构标准图层输出”；确认轮廓识别准确后再进入配筋阶段。

如果 AutoCAD 安装在其他目录，可覆盖项目属性：

```powershell
dotnet build .\AutoCAD二次开发.slnx -p:AutoCADInstallDir="C:\Program Files\Autodesk\AutoCAD 2020"
```

## 实例1钢筋阶段（1.0.0.20）

加载 `artifacts\Debug\AutoCADPlugin-1.0.0.20.dll`，执行 `SD_VERSION` 核实版本。
旧 DLL 已加载时请保存图纸并重启 AutoCAD 后加载新版；同名程序集无法可靠地在同一会话热替换。

1. 已有四倍结构轮廓：直接执行 `SD_REBAR`，框选完整结构大样轮廓，回车。
2. 尚未生成轮廓：先执行原来的 `SD_AUTO_DETAIL`，再执行 `SD_REBAR`。
3. 程序匹配阶梯檐口、960 宽竖向构造和斜屋面，添加 7 条连续纵筋、23 个点筋。
4. 原有轮廓、支撑叉号和说明不改动。一次 UNDO 可撤销本次新增钢筋；发现原有钢筋时拒绝叠加。

本版是用户提供实例1的专用几何配筋规则，不是任意构件的自动结构设计。左侧檐口尺寸、坡度和支撑深度必须匹配参考；仅支持平移，允许屋面截断长度改变。匹配失败不生成；旋转、镜像、不同檐口尺寸需新增规则。

纵筋来自目标 DWG 的 7 条完整多段线路径，保留折点、收头和伸入支撑的锚固表达；不根据支撑框自行生成支撑钢筋。屋面部分随当前屋面截断长度沿坡向调整。

点筋已由用户确认**完全复制目标 DWG**：两个 bulge=1 的半圆多段线，中心线直径 50、多段线宽度 50，因此可见外径为 100。不是普通直径50的 CIRCLE，也不是短直线。颜色采用 S-REIN-POINT（ACI30）；纵筋 S-REIN（ACI6洋红）多段线宽35。两图层使用连续线型，实体颜色/线型/线宽随层，图层线宽为默认值。开启 FILLMODE 显示实心。

来源记录：`tests/fixtures/example1-target-geometry.json`。配筋数据内置于 DLL，运行不依赖桌面参考文件路径。`Example1RebarData.cs` 保存相对屋面转角的原始钢筋顶点；`Example1Rebar.cs` 负责轮廓核对、屋面长度适配和实体创建。

验证：在 AutoCAD 2020 accoreconsole 中用原始建筑 DWG 提取轮廓并放大4倍，检查匹配、30个实体、35宽纵筋、点筋圆弧/宽度、实际外径100、重复布筋拦截及错误比例/空选择/多个大样拒绝。输出见 `tests/rebar-results.txt` 和 `tests/example1-rebar-check.dwg`。


## 支撑名称自动分类（1.0.0.21）

SD_AUTO_DETAIL 按每个 SD-REGION 的图形 X/Y 方向包围框计算宽高：高 > 2×宽为楼层梁；宽 > 2×高为楼层板。其余情况（包括恰好两倍）保留楼层梁。放大四倍不影响比例；已有图中文字不追溯修改，重新生成轮廓时生效。

