# DeskCal 1.0.6 · Acrylic 真机诊断

本包从 LilyS-eng/DeskCal 的 windows-1.0.6（d1f609a）派生。运行 ZIP 根目录的 DeskCal.exe。版本号和日历数据目录沿用 1.0.6。请先从托盘退出其他 DeskCal。使用原来能稳定复现灰色的 Windows 11 x64 电脑、桌面背景及设置。

## 记录同一次运行

使用 Ctrl+Alt 加键盘上排数字。快捷键可在 DeskCal 未获得焦点时使用。若被其他软件占用，可用托盘右键 → Acrylic 诊断 → 对应标记，但打开菜单会改变焦点，请在反馈时注明。按照眼睛看到的状态标记，结果不符合预期时用 8 或 9。

|动作|标记或快捷键|
|---|---|
|冷启动后先不点窗口，观察灰色|1 · COLD_START_GRAY|
|将要真人拖动右下角，尚未按下鼠标|2 · BEFORE_MANUAL_RESIZE|
|拖动一点后松手，确认 Acrylic 已出现|3 · MANUAL_RESIZE_ACRYLIC_OK|
|点击 ×，再用托盘左键展开；确认灰色|4 · TRAY_RESTORE_GRAY|
|再按 2，拖动一点；确认 Acrylic 已恢复|5 · TRAY_RESIZE_ACRYLIC_OK|
|用顶部拖动条将窗口**边框**拖至工作区左或右边缘，松手；点击边条展开，确认灰色|6 · EDGE_RESTORE_GRAY|
|再按 2，拖动一点；确认 Acrylic 已恢复|7 · EDGE_RESIZE_ACRYLIC_OK|
|其他时刻观察到灰色或正常 Acrylic|8 · OBSERVED_GRAY / 9 · OBSERVED_ACRYLIC_OK|

每阶段顺便观察顶部拖动条空白处和网页卡片是否同时变色。不要用打开设置、截图弹窗或点窗口来代替标记。如果出现材质的时间点在鼠标按下、移动第一步、持续拖动或松开，回传日志时可以一并注明。

**可选 A/B（放在以上基线操作之后）。** 再次从托盘恢复到灰色并按 4。右键托盘 → Acrylic 诊断 → A/B 实验，切换 WebView2 的可见性；WPF 留下原有布局空间。观察露出的原网页区域及顶栏，按 8 或 9 标记。再切换一次恢复浏览器并标记。说明灰色是仅网页区域、仅顶栏、还是整个窗口；注明打开菜单时外观有无改变。此实验会改变浏览器可见性和焦点，单次恢复不能证明是哪个组件的错误。

从托盘选择“退出 DeskCal”。在资源管理器地址栏打开 **%LOCALAPPDATA%\DeskCal\logs**，回传本次进程的 **acrylic-diagnostic-年月日-时分秒-毫秒-进程号.log**。只需该 log，若方便可附 Windows 版本/系统内部版本号及缩放。日志不采集屏幕图像、日程文本和 localStorage 内容。

## 已证实 · 1.0.6 的三条路径

|路径|入口与状态变化|关键边界|
|---|---|---|
|冷启动|Application.Run(new Widget) → 创建主 HWND → SourceInitialized：取得 HwndSource、加 TOOLWINDOW、设圆角、cloak、Apply → WPF Loaded → 创建 WebView2 环境和 controller、导航本地页面 → 页面 appearance/初始尺寸与 NavigationCompleted 就绪 → Rendering → Dispatcher.ContextIdle → DwmFlush → uncloak|两个就绪条件的先后不固定。WPF IsVisible 可以在 DWM 仍 cloak 时为 true。ContentRendered 在基线没有业务处理。|
|托盘 / 贴边恢复|托盘隐藏：切换 placement、取消待呈现、cloak、Window.Hide。贴边隐藏：先隐藏主窗再显示独立 handle HWND。恢复时 PrepareShow 隐藏 handle、更新状态 → WindowState.Normal → Show 原 Window → QueueReveal → Rendering / ContextIdle → Flush / uncloak → Activate、web.Focus|两种恢复共享 Recover。代码没有 Dispose 或重新创建主窗口、WebView2 或 controller；实际句柄是否重建要看快照。|
|成功的真人 resize|右下角 HTML grip → pointerdown、setPointerCapture → pointermove → requestAnimationFrame → 网页发 size preview → 宿主 Resize 裁剪工作区 → 位置/尺寸确有变化才 SetWindowPos → WPF / WebView2 重新布局；丢失指针捕获后 size save → 回写尺寸并 render|输入发生在已显示的浏览器内。ResizeMode.NoResize，不是 Windows 非客户区的系统边框拖拽。网页 viewport resize 也执行 applySettings，appearance 消息和宿主 Apply 都有去重条件。|

- 主窗为普通无边框 WPF Window，AllowsTransparency=false，未使用 WindowChrome、透明分层输入、桌面嵌入或 WebView2 composition controller。顶部 30 DIP 是 WPF 拖动行，下方为常规 windowed WebView2。基线没有自定义 WM_SIZE / NCCALCSIZE / NCHITTEST / ACTIVATE / DWM 消息处理器，亦没有自定义 OnStateChanged、SizeChanged 重挂材质逻辑。
- WindowAppearance.Apply 在启用玻璃时先让 HwndTarget 和 Window 透明，再扩展四边 -1，并请求 SystemBackdrop=3。请求失败或玻璃设置关闭时，它明确关闭材质、收回边距并设置实体底色。缓存按请求的 glass/darkText 缓存，不能用 AcrylicActive 证明当前屏幕像素。正常恢复没有再次显式设置不透明背景。
- 根 DockPanel 没有填充；WebView2 初始化前已设置透明 DefaultBackgroundColor；网页 body 的 Windows widget 规则覆盖普通网页的深色底，日历卡片本身仍按 --alpha 绘制半透明底并使用 CSS backdrop-filter。没有查到 Show 时应用主动改写这些颜色的代码。WPF、网页、子 HWND 及 DWM 的合成结果仍需分别观察。
- 1.0.7 在 cloak 期间改变窗口高度、更新布局和浏览器位置再还原；用户真机仍灰。能排除该流程足以激活 Acrylic，不能据此确定某一次 WM_SIZE、DwmFlush 或特定 GPU surface 本身出错。

## 高概率怀疑 · 优先区分的两层

问题集中在“隐藏或创建后第一次呈现”与“已可见时带真实网页输入和尺寸改变的呈现”之间。按用户观察，一旦正常，背景动态响应基本正常。先检查主 HWND / HwndTarget 的材质和背景是否在 Show 后被覆盖，再检查浏览器子表面是否遮住正常主窗。鼠标激活、网页重绘和可见状态下的 bounds 变化在真人拖动中同时发生，目前没有证据在三者中确定一个原因。

## 尚不知道 · 如何读日志

1. 按同一进程的 MARK 对齐灰色和正常状态。先比较主 HWND、source / target 身份、rect、DPI、可见性、style/exstyle、placement、DWM 属性 14/20/33/38（cloaked / dark mode / corner / backdrop）及 getter HRESULT。再看每个 DwmSet、Extend、Flush 的实参/结果与 Apply 缓存是否命中。hr 非零的 getter 值不可用。
2. 比较 windowBg、targetBg、panelBg、根 visual、测量/排列有效性；比较 browser controller 身份、parent HWND、bounds、bounds mode、raster scale、visible、default background、子 HWND 树的 class/parent/位置/样式。窗口 rect 为屏幕像素，client 为客户区像素，WPF 尺寸为 DIP；controller bounds 按 bounds mode 解释。MARK 后另有异步 DOM 快照，记录 viewport、DPR、焦点和 html/body/卡片的计算背景。
3. 若灰色时 backdrop 变为 1 或出现实体背景，再沿时间戳查最后一次变化。若属性相同而顶栏正常、隐藏浏览器后原网页区域也正常，浏览器路径的优先级上升。若隐藏浏览器后整个已暴露客户区仍灰，优先调查主窗 / DWM。A/B 操作若自行触发恢复，单独记录，不据此归责。
4. 所记录的 margin 是应用最近发出的请求和返回值。没有相应公开 getter 读回 DWM 当前四边扩展值；extended frame bounds 也不能冒充 margins。子 HWND 身份与 controller 身份是可观察代理，不能读取私有 DirectComposition surface ID，不能由 HWND 不变推断 swap chain 没变。Rendering、DwmFlush、DWM getter 成功也不能证明目标帧已按 Acrylic 呈现。没有视觉标记或底层呈现证据时根因仍开放。必要时再围绕标记做定向 GPU / DWM 跟踪。
5. 消息钩子从 SourceInitialized 注册；之前的 HWND 创建消息不可回放。消息状态是调用点采样，后续 WPF 布局可能尚未完成。WM_NCHITTEST 取样，LayoutUpdated 只记几何或排列有效性变化。异步磁盘写入减轻干扰，但诊断采样仍可能影响时序；若问题在诊断版消失，请如实反馈。

## 1.0.7 Edge Dock 静态对照

1.0.6 的 DragMove → AfterDrag 根据主窗口两侧边框与该窗口所在显示器工作区的距离（24 DIP）判断。1.0.7 的 Drag 改成松手时鼠标到鼠标所在显示器工作区边缘的距离，并提前拒绝窗口左上坐标未变的操作。以 620 DIP 窗口为例，从顶栏中间抓取时，即使左边框正好贴边，鼠标仍可距边约 310 DIP，远超 24 DIP。旧手势会稳定落空；该差异不能证明所有鼠标已经到边仍失败的情形。诊断版保留原 DragMove、AfterDrag、工作区计算、HideToEdge / PrepareShow 和 handle 回调。
