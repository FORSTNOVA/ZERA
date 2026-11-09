# Android 本地验收

仅使用合成文件和本地服务，不需要真实网盘账号。默认目标为 `emulator-5554`，不要将脚本指向个人手机。

1. 构建测试 APK：`dotnet build src/ZERA.Android/ZERA.Android.csproj -c Release -p:DefineConstants=ZERA_TESTING`。需要已安装的 .NET Android workload、Android SDK 和 JDK 21；可按本机配置传入 SDK/JDK 属性。
2. 使用 `adb -s emulator-5554 install -r <测试APK>` 安装，启动 `com.zera.downloader`。
3. 启动 `python tests/AndroidSmoke/fixture.py`；BT 测试另使用项目的 `src/ZERA.Core/Runtime/win-x64/python.exe` 运行 `tests/AndroidSmoke/swarm.py`。
4. Node.js 24 执行 `node tests/AndroidSmoke/run.mjs`。设置 `ZERA_BT_FIXTURE` 为生成的 `scratch/mobile-swarm/fixture.torrent` 绝对路径可包含普通、2/4/8 会话及私有种子测试。
5. 设置 `ZERA_UI_ONLY=1` 可只运行界面、主题和已有 BT 结果文件导出读取验收。

输出为 `scratch/android-acceptance/` 下的 JSON 和截图。脚本会修改模拟器中的测试账号、设置与下载历史，使用单独的测试模拟器。

新增功能验收：`node tests/AndroidSmoke/round2.mjs` 覆盖本地 QR 图片、模拟夸克转存/删除保护、容量与子会话独立控制。测试 APK 中的夸克请求全部由内存 HttpMessageHandler 响应，不访问真实账号。子会话控制检查应在本地 swarm 做种服务启动前执行，避免小文件过快完成。

`node tests/AndroidSmoke/exports.mjs` 覆盖公共导出、自动导出、通知栏按钮与本地删除。先在模拟器上通过真实 `system.pickExportFolder` 系统选择器选取专用的 `Documents/ZERA-Test` 文件夹并授权；脚本只允许校验该目录下的合成副本。运行本地 HTTP fixture，允许模拟器通知权限，保持通知栏可操作。脚本会通过真实通知栏 PendingIntent 点击“全部暂停”和“继续任务”，不会为测试而把服务改为 exported。

新增功能结果写入 `scratch/android-round2-acceptance/`。重启测试等待网页初始化完成再注入 CDP 回调，使用明确的 Activity 启动命令以排除启动器与权限弹窗的干扰。

`ui-round2.mjs` 验收新增按钮与 360px 布局；图片选择器取消时等待真实系统选择器结束并返回应用前台。
`overlay.mjs` 在模拟器临时授予特殊悬浮窗权限，检查原生窗口和真实触摸按钮，完成后恢复权限。
所有脚本仅接受 `emulator-*` 目标。多次连接同一 WebView 时保留原始回调，避免测试钩子相互包装造成递归。

交付包通过 `scripts/build-android.ps1` 构建，默认 Release，不定义 `ZERA_TESTING`；测试桥接入口与 WebView 远程调试不会包含在交付版本中。
