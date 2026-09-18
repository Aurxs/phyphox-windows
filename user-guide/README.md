# phyphox Windows 使用指南生成源

`phyphox-Windows-使用指南.pdf` 是独立的 8 页中文教程；不属于程序安装目录。教程与 2026-09-18 新版界面及浏览器媒体需求变更对应，采用本地浏览器真实截图。麦克风、普通相机与扬声器优先浏览器授权；BLE/USB与主计算、存储仍属服务。该截图不是物理设备测试，也不是新版 Windows 界面的验收证明。

## 文件

- `build_guide.py`：完整生成源，所有资源路径相对脚本位置。
- `assets/ux-experiment-dark.png`：公式计算工作台的真实新版截图（输入 2、结果 4），不含模拟硬件数据。
- `requirements.txt`：生成 PDF 所需 Python 包。
- `phyphox-Windows-使用指南.layout-check.json`：各页排版结束位置；生成时检查正文、表格、提示框是否越过页脚保留区域。

## 重建

需要 Python 3 和具有中文字符的 TrueType 字体（TTF 或含 TrueType 的 TTC）：

```sh
python -m pip install -r requirements.txt
python build_guide.py
```

脚本会尝试 macOS 黑体、Windows 微软雅黑或 Linux 文泉驿。找不到时指定字体；若未指定粗体，使用同一字体：

```sh
python build_guide.py --font /path/to/chinese.ttf --bold-font /path/to/chinese-bold.ttf
```

可用 `--output /path/to/guide.pdf` 改变输出位置。字体不随本目录分发；请确保使用者有权使用及嵌入所选字体。不同字体可能改变换行，脚本如报溢出应调整版面，不应关闭布局检查。

## 本次快速验证

使用 ReportLab 生成，Poppler 将 8 页渲染后检查新版截图、修改后的按钮和操作说明、页脚及表格边界；未发现裁切或重叠。另检查 8 页和关键修改文字。不运行应用全量测试，不把本次文档验证表述为设备、Windows 10 或新版 Windows 实机测试通过。PDF 中旧界面 Windows 11 ARM/x64 仿真验证记录保留为历史说明。

进一步修改时可用 `pdftoppm -png -r 120 phyphox-Windows-使用指南.pdf preview` 渲染改动页并检查。除字体选择与输出路径外，生成不需要原工作区或临时目录。

本次媒体需求更新复用生成脚本，聚焦重新渲染检查第 3、6、7、8 页，未发现溢出或重叠；第 7 页操作依据实际 BrowserMediaPanel / BrowserSpeakerPanel，不把生成 PDF 当作实物或浏览器媒体验收。
