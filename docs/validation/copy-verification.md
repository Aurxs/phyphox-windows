# 定向文案 QA — 2026-09-18

仅在独立服务 `127.0.0.1:37654`、headless `copy-qa` 检查当前文案。资源为 `index-NUu8UNMX.js` / `index-CXoLl8iU.css`。服务初始尚未监听时曾拒绝连接，待启动完成后正常，未计为功能失败。

- 官方“加速度 (含 g)”直接显示“需要加速度传感器（含重力 g），当前尚未接入。”
- 官方“磁力计”直接显示“需要磁场传感器（磁力计），当前尚未接入。”
- 两项提示均直接可见，无需展开；开始按钮均 disabled，未出现“输入 #0 sensor”或“禁止模拟”。
- 媒体页 7 个 details 全部默认关闭；排除折叠内容后可见正文约 207 字符，原大段限制与计数已收起。截图检查表单、启停按钮与状态清晰，无横向溢出；未对旧版本做可比字符数采样，不报告百分比。
- getUserMedia 调用 0、AudioContext 构造 0。没有启用权限、声音或任何实物设备。

截图：`sensor-friendly.png`、`copy-magnetometer.png`、`media-concise.png`。结构化证据：`copy-qa-result.json`。仅关闭了 copy-qa 浏览器，未操作用户的 phyphox-preview / phyphox-ux；未重跑业务、Windows VM 或硬件测试，未修改代码。
