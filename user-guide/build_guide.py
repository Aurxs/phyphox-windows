from pathlib import Path
from html import escape
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib.colors import HexColor, white
from reportlab.platypus import Paragraph, Table, TableStyle
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.utils import ImageReader

import argparse
import os
parser=argparse.ArgumentParser(description='Generate the standalone Chinese user guide.')
parser.add_argument('--font', help='Chinese TrueType font or TTC (first face).')
parser.add_argument('--bold-font', help='Bold Chinese font; defaults to regular font.')
parser.add_argument('--output', type=Path)
args=parser.parse_args()
BASE=Path(__file__).resolve().parent
OUT=args.output or BASE/'phyphox-Windows-使用指南.pdf'
SHOT=BASE/'assets/ux-experiment-dark.png'
candidates=[Path('/System/Library/Fonts/STHeiti Light.ttc'), Path(os.environ.get('WINDIR','C:/Windows'))/'Fonts/msyh.ttc', Path('/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc')]
font=Path(args.font) if args.font else next((p for p in candidates if p.is_file()),None)
if font is None:
 raise SystemExit('A Chinese TrueType font is required. Pass --font /path/to/font.ttf (or .ttc).')
bold=Path(args.bold_font) if args.bold_font else (Path('/System/Library/Fonts/STHeiti Medium.ttc') if font==candidates[0] else font)
pdfmetrics.registerFont(TTFont('CN',str(font),subfontIndex=0))
pdfmetrics.registerFont(TTFont('CNB',str(bold),subfontIndex=0))
pdfmetrics.registerFontFamily('CN',normal='CN',bold='CNB',italic='CN',boldItalic='CNB')
W,H=595.276,841.89; M=46; CW=W-2*M
INK=HexColor('#202A35');MUTED=HexColor('#536272');ORANGE=HexColor('#D96513');LINE=HexColor('#DDE3E8');PALE=HexColor('#FFF3E8');BLUE=HexColor('#EEF3F8')
c=canvas.Canvas(str(OUT),pagesize=(W,H));c.setTitle('phyphox Windows 使用指南 - 开发验证版');c.setAuthor('phyphox Windows 移植项目');c.setSubject('便携运行、基本操作、数据与设备配置、试用检查')
y=0;page=0; checks=[]

def para(text,size=10.5,leading=17,color=INK,after=8,bold=False,width=None):
 global y
 style=ParagraphStyle('p',fontName='CNB' if bold else 'CN',fontSize=size,leading=leading,textColor=color,wordWrap='CJK',spaceAfter=0)
 p=Paragraph(text,style);w,h=p.wrap(width or CW,1000)
 if y-h<58:raise RuntimeError(f'page {page} content overflow: {text[:45]} at {y-h}')
 p.drawOn(c,M,y-h);y-=h+after

def title(n,t,sub):
 global y,page
 if page:c.showPage()
 page+=1;y=H-46
 c.setFillColor(ORANGE);c.rect(M,y-7,26,4,fill=1,stroke=0)
 c.setFont('CN',9);c.setFillColor(MUTED);c.drawString(M+36,y-6,'phyphox Windows  |  使用指南')
 c.setFont('CN',8);c.drawRightString(W-M,y-6,'开发验证版 · 0.1.0-dev')
 y-=51
 para(f'{n}  {t}',23,29,after=10,bold=True)
 para(sub,10,16,MUTED,after=18)
 c.setStrokeColor(LINE);c.line(M,43,W-M,43)
 c.setFillColor(MUTED);c.setFont('CN',8);c.drawString(M,29,'版本日期 2026-09-18  |  独立移植，非 phyphox 官方 Windows 发布')
 c.drawRightString(W-M,29,f'{page:02d} / 08')
 c.bookmarkPage('page'+str(page));c.addOutlineEntry(t,'page'+str(page),0,False)

def section(t):
 global y
 y-=4;para(t,13,20,after=8,bold=True)

def box(head,text):
 global y
 ps=ParagraphStyle('box',fontName='CN',fontSize=10,leading=16,textColor=INK,wordWrap='CJK')
 p=Paragraph('<b>'+head+'</b><br/>'+text,ps);pw,ph=p.wrap(CW-26,1000);h=ph+24
 if y-h<58:raise RuntimeError('box overflow')
 c.setFillColor(PALE);c.roundRect(M,y-h,CW,h,6,fill=1,stroke=0)
 c.setFillColor(ORANGE);c.rect(M,y-h,3,h,fill=1,stroke=0)
 p.drawOn(c,M+13,y-h+12);y-=h+14

def step(n,head,text):
 para(f'<font color="#D96513">{n:02d}</font>  <b>{head}</b><br/>{text}',10.5,17,after=8)

def table(headers,rows,widths):
 global y
 ps=ParagraphStyle('cell',fontName='CN',fontSize=9.6,leading=15,textColor=INK,wordWrap='CJK')
 data=[[Paragraph('<b>'+v+'</b>',ps) for v in headers]]+[[Paragraph(v,ps) for v in row] for row in rows]
 t=Table(data,colWidths=[CW*x for x in widths],hAlign='LEFT')
 t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),BLUE),('VALIGN',(0,0),(-1,-1),'TOP'),('LEFTPADDING',(0,0),(-1,-1),10),('RIGHTPADDING',(0,0),(-1,-1),10),('TOPPADDING',(0,0),(-1,-1),8),('BOTTOMPADDING',(0,0),(-1,-1),8),('LINEBELOW',(0,0),(-1,0),.7,LINE),('LINEBELOW',(0,1),(-1,-1),.35,LINE)]))
 tw,th=t.wrap(CW,1000)
 if y-th<58:raise RuntimeError(f'table overflow page {page} {y-th}')
 t.drawOn(c,M,y-th);y-=th+13

def code(lines,size=9.2):
 global y
 lines=lines.splitlines();h=len(lines)*14+22
 if y-h<58:raise RuntimeError('code overflow')
 c.setFillColor(BLUE);c.roundRect(M,y-h,CW,h,5,fill=1,stroke=0)
 for i,line in enumerate(lines):
  if pdfmetrics.stringWidth(line,'CN',size)>CW-24:raise RuntimeError('code line too wide')
  c.setFont('CN',size);c.setFillColor(INK);c.drawString(M+12,y-16-i*14,line)
 y-=h+12

def endpage():checks.append({'page':page,'bottom_y':round(y,1)})

# 1
title('01','拿到压缩包后，怎样开始','第一次试用不需要外部设备。先完成本页启动步骤，再按第 2 页验证计算。')
box('你需要的是便携程序包','文件名：phyphox-windows-x64-portable-dev.zip。源码 ZIP 用于开发，不是直接双击运行的程序包。')
step(1,'解压完整文件夹','把 ZIP 解压到电脑或可写 U 盘。进入解压后的 phyphox-windows 文件夹，找到 start-phyphox.cmd。不要在压缩包内直接运行，也不要只复制 EXE。')
step(2,'启动本地服务','双击 start-phyphox.cmd。运行期间保留终端窗口；程序会显示“浏览器打开：”及本机地址。正常使用不需要安装 .NET、SDK 或 Node.js。')
step(3,'在浏览器打开地址','复制终端实际显示的 http://127.0.0.1:端口 到浏览器地址栏。端口可能每次不同，不要把本指南中的示例端口当作固定地址。')
step(4,'确认连接成功','页面右上角应显示“本地服务已连接”。从左侧“实验库”进入，先选择“公式计算工作台”。')
section('运行前确认')
table(['项目','使用条件'],[
('电脑与浏览器','建议先在 Windows 11 上试用，电脑需已有浏览器。4 核 / 8 GB 是规划基线，不是已通过的性能指标。'),
('验证范围','Windows 11 ARM 虚拟机中以 x64 仿真运行：新版 Edge 界面与宽屏检查通过；真实媒体未验收。Windows 10 与 Windows 11 x64 实机仍待验收。'),
('离线与设备','核心实验不依赖账号、云端或手机。网络实验需要其指定网络；外设仍需正确驱动与协议。')],[.22,.78])
para('阅读路线：第 2 页试算；第 3 页日常操作；第 4-5 页数据；第 6-7 页设备；第 8 页排障。',9,15,MUTED)
endpage()

# 2
title('02','先用公式工作台试算','这是纯计算样例，不采集设备数据。以下为新版界面的本地浏览器截图。')
i=ImageReader(str(SHOT));iw,ih=i.getSize();dh=270;dw=dh*iw/ih
c.drawImage(i,M+(CW-dw)/2,y-dh,width=dw,height=dh);y-=dh+9
para('界面示例：输入 x = 2，右侧 x² 显示 4.0000。',9,14,MUTED,after=13)
step(1,'检查初始值','在“实验库”搜索“公式”，打开“公式计算工作台”。确认输入为 2，结果为 4（可能显示为 4.0000）。')
step(2,'修改参数','将输入改为 3，按 Enter 或点击输入框外提交。结果应更新为 9；纯计算样例在未启动采集时也可计算。')
step(3,'检查控制按钮','点击“开始”后应显示“测量中”；点击“暂停”后显示“已暂停”，主按钮变为“继续”。需结束记录时，点击右上角三点“实验说明与更多操作”，选择“停止实验”。')
step(4,'检查清空','点击垃圾桶图标“清空数据”，确认范围或先保存。清空按实验定义重置数据和时间；本样例输入恢复为 2，可能需提交输入或开始后再次计算。')
box('数值不变，不一定是故障','公式样例只在输入变化等触发条件下重新计算。没有真实采集源时，不会为了演示自动生成测量曲线。')
endpage()

# 3
title('03','选择实验与日常操作','实验文件能打开，不代表电脑具备运行它需要的硬件。先看提示，再开始测量。')
table(['左侧入口','主要用途'],[
('实验库','搜索实验；用“导入实验”选择 .phyphox 或包含资源的 ZIP。'),
('当前实验','查看数值、分页图表，修改参数，开始 / 暂停 / 停止 / 清空，并导出结果。'),
('连接设备','查看平台能力，显式扫描、选择和连接设备，配置输入或控制输出。'),
('数据与回放','保存与恢复服务端快照，导入数据，查看完整记录的独立重算结果。'),
('音频与摄像头','优先使用浏览器授权的麦克风、相机与扬声器；原生媒体保留在高级入口。')],[.25,.75])
section('打开或导入实验')
para('实验按分类排列，可用“全部实验 / 官方实验 / 入门与示例 / 我的实验”筛选。导入后在“我的实验”查找；ZIP 应保留附属资源目录结构。目标格式为 .phyphox 1.20，较新格式不自动保证兼容。')
para('有数据时切换实验，会提示取消、直接切换或保存后切换；正在测量时先暂停。若“开始”不可用，查看按钮下方设备提示，选择“设置音频与相机”或“连接设备”。')
section('控制与图表')
table(['操作','实际含义'],[
('开始 / 暂停 / 停止','启动配置好的实验；暂停保留数据以便继续；停止保留结果并封口当前可完整记录的日志。'),
('清空','重置实验时间及相应数据容器；保护组和分组清空遵循实验定义，不等于删除全部磁盘文件。'),
('分页与图表','切换实验视图页；图表可缩放、平移、拾取或导出 PNG，具体可用功能取决于实验定义。'),
('显示与原始数据','界面最多显示每个容器最近 20,000 项，保存与数据导出使用完整缓冲区。历史叠加最多保留 32 组浏览器收到的变化。')],[.25,.75])
para('右上角可切换语言和深浅主题。界面中文 / 英文切换不代表所有原版实验文字已完成翻译。',9.5,16,MUTED)
endpage()

# 4
title('04','保存、恢复、回放与导出','先分清“继续查看当前数据”和“向别人交付结果”，再选择保存方式。')
section('保存与恢复快照')
step(1,'保存','点击实验页“保存与导出”（下载图标）或底部“保存结果”，选择“保存到本机”。也可在“数据与回放”点击“保存当前快照”；快照保存在本地服务的数据目录。')
step(2,'恢复','在“快照编号”中选择项目，点击“恢复此快照…”，核对后“确认恢复并覆盖”。这会停止运行并替换当前数据，请先保存重要结果；恢复为暂停状态，不自动恢复设备连接。')
section('查看独立回放')
para('运行并停止实验后，在“数据与回放”点击“刷新记录”。对标为“完整日志”的项目，点击“独立重算并查看”。回放结果单独展示，不替换当前实验，也不会向硬件重新发送指令。')
para('录制中、不完整或缺少历史的记录不能当作完整回放。当前回放是记录输入的重新计算，尚不是可调速度的逐帧播放器。',9.5,16,MUTED)
section('在“保存与导出”中下载结果')
table(['导出方式','得到什么 / 怎样使用'],[
('下载实验状态 .phyphox','保存实验定义、当前容器数据与时间状态。它不是完整设备检查点；若有附属资源，也需一并保留。'),
('CSV','下载的是 ZIP 压缩包，里面包含数据 CSV 与元数据。解压后再用表格软件打开相应 CSV。'),
('XLSX','下载 Excel 工作簿；检查列名、单位以及数据是否符合本次实验。'),
('PNG','导出图表的显示快照，便于发图或写报告。图片不替代完整数值数据。')],[.27,.73])
box('备份与退出','重要结果请主动导出，并在停止服务后备份整个 data 文件夹。结束时先停止实验，再在终端按 Ctrl+C；关闭网页本身不等于停止本地服务。待程序退出后再安全拔出 U 盘。')
endpage()

# 5
title('05','把已有数据导入实验','CSV / TSV / WAV 是数据文件；.phyphox / 实验 ZIP 是实验定义，两者入口不同。')
para('先打开目标实验并停止测量，再进入“数据与回放”的“导入已有数据”。表单中明确文件列或通道对应哪个容器；表单列号从 1 开始，高级 JSON 索引仍从 0 开始。')
section('可照做的 CSV 试用例子')
para('这是导入计算练习，不是真实测量。加载“公式计算工作台”，将下面内容保存为 UTF-8 文本 sample.csv：')
code('value\n5')
step(1,'选文件并填写映射','选择 sample.csv；文件列填 1，目标容器选 x，保留首行为标题；倍率 1、偏移 0，时间选“不指定时间”。')
step(2,'选择写入方式','选择替换数据，勾选“导入后执行一次分析”，点击“导入到当前实验”。若目标已有数据，核对提示中的实验和容器，再“确认覆盖并导入”；取消不会导入。')
step(3,'查看结果与来源','返回“当前实验”，应看到 x = 5、结果 25，并提示当前是导入数据。已有重要结果请在导入前先保存快照。')
section('其他数据的映射规则')
table(['文件或设置','注意事项'],[
('CSV / TSV','目标容器必须存在。CSV 可选分隔符，TSV 使用制表符；按文件设置标题行、单位、倍率与偏移。'),
('时间列 / 采样率','秒单位的时间列与已知正采样率二选一，还需独立时间容器。未知时选不指定时间，不猜采样率。'),
('WAV','选择实际存在的音频通道与目标容器，也可映射时间轴。采样率来自文件；服务检查通道，仅支持已实现的 PCM / 浮点 WAV。')],[.26,.74])
para('复杂映射可用高级 JSON。原文件与映射回执保存在 data/imports；保留来源和单位，避免把导入结果误认为实时采集。',9.5,16,MUTED)

endpage()

# 6
title('06','BLE 与 USB：先有协议，再连接','当前没有任何真实外设型号通过本项目验收。没有设备时，先跳过本页的连接操作。')
box('BLE / USB 仍由本地服务连接','BLE、USB 串口和 HID 的扫描、连接、收发及控制由本地服务完成，不需要 Web Bluetooth、WebUSB 或 Web Serial。麦克风、普通相机和扬声器另可使用浏览器授权入口，见第 7 页。')
step(1,'准备设备资料','确认型号、固件、协议与驱动。BLE 需要服务 / 特征 UUID、读写方式和数据格式；USB 需要先确定串口、HID、WinUSB 或专用 SDK 类别。')
step(2,'扫描并明确选择','进入“连接设备”，选择对应通信类型并扫描。只选择你确认的实际设备；看到设备名称不等于已经知道它的数据协议。')
step(3,'按协议填写配置并连接','根据设备文档填写串口、HID 或 BLE 参数表单，复杂配置可展开高级 JSON。不要套用其他型号参数。原版 BLE 输入需关闭自动订阅（subscribe=false），由实验 XML 管理订阅。')
step(4,'绑定实验输入','打开目标实验后，选择对应输入槽。原版 BLE 可按 XML 绑定；通用外设映射需明确容器、转换、帧结构和时间。替代手机传感器时还需确认单位、坐标与校准。')
step(5,'开始并核对真实数据','确认阻断提示解除后开始实验。用已知输入或参考仪器核对数值；若断线、数据不合理或解析异常，停止测量并保存报错信息。')
table(['设备类别','当前支持条件'],[
('BLE','GATT 读 / 通知 / 写等适配已有实现；型号、配对、MTU 与重连行为仍需实测。'),
('USB 串口','必须知道波特率、校验等参数以及固定包长或分隔符，不能把一次读取直接当作一帧。'),
('USB HID','必须知道 Report ID、报告长度与字段协议，不等于所有 HID 都能直接作为实验输入。'),
('WinUSB / 专用 SDK','当前没有通用支持；需按驱动、接口、端点和厂商协议另做适配。')],[.27,.73])
para('控制输出、手动十六进制写入与实验配置可能改变真实设备状态。只有明确理解协议时才发送；失败或断线后的控制命令不会自动重放。',9.5,16,MUTED)
endpage()

# 7
title('07','浏览器媒体与高级接口','最新约定允许浏览器使用麦克风、普通相机和扬声器；实验计算、状态和保存仍在服务。')
section('先准备来源，再开始实验')
para('进入“音频与摄像头”不会自动打开设备。先加载对应实验，点击“刷新设备”，选择设备及实验输入，再明确授权。设备名可能在授权后才显示；需使用 localhost 或安全连接。')
table(['入口','操作与边界'],[
('麦克风','点击“授权并连接麦克风”。实际录音和PCM传送此时开始；服务待机不写容器，实验“开始”后才测量。核对真实处理采样率，音频拥塞会停止以避免缺样。'),
('普通相机','选择设备和输入，在“图像设置”调整宽高/帧率，点击“授权并连接摄像头”。实际帧送服务做ROI与颜色分析；请求参数不保证实现，时间为服务接收时刻。'),
('扬声器','选择实验音频输出，调节音量（初始10%），点击“启用扬声器”，无自动测试音。开始实验才播放服务生成的循环声音；当前使用系统默认输出。')],[.2,.8])
para('顶部会标记媒体正在使用。暂停保留来源、停止写入测量数据，扬声器静音；停止、清空或切换实验后需重新启用。可随时“停止实验并断开设备”或“停止声音和实验”。关闭页面释放设备；隐藏浏览器标签会停止扬声器，采集也可能受后台节流影响。',10,16)
box('不能当作已校准测量','真实录音、播放与相机尚未验收。浏览器可能重采样或处理音频；普通图像不保证曝光、物理亮度或光谱标定，不提供深度。仅支持已实现的循环音频；非循环逐分析输出仍不支持，动态改参与同步需实测。')
section('原生高级入口与网络')
para('“高级：Windows 原生音频与相机接口”保留WASAPI / MSMF。相机探测会短暂打开设备；保存配置不开始采集，曝光物理参数必须实测。HTTP / MQTT由服务连接实验指定地址，需相应网络，其他核心能力可离线使用。',10,16)
para('数据仍在程序目录 data：experiments 存实验，snapshots 存快照，recordings 存日志，imports 存导入文件。下载文件进入浏览器下载位置；移动电脑后重新核对设备与权限。',9.5,15,MUTED)
endpage()

# 8
title('08','遇到问题与反馈','先记录现象，再改变配置。以下顺序适合朋友收到开发版后进行一次快速试用。')
table(['现象','先检查什么'],[
('终端闪退 / 服务启动失败','确认已经完整解压、EXE 与资源未分开、目录可写。保留终端错误截图；不要仅凭闪退判断为缺 .NET。'),
('网页打不开 / 等待本地服务','确认终端仍运行；使用本次终端显示的地址和端口。旧地址或旧页面可关闭后重新打开。'),
('开始按钮不可用','阅读实验页面的能力与设备提示；实验可能需要电脑没有的传感器或尚未支持的功能。'),
('公式修改后结果没更新','按 Enter 或点击输入框外提交；检查服务连接与页面错误。'),
('找不到导出文件','查看浏览器下载列表；CSV 导出实际为 ZIP。服务端快照则在 data/snapshots。'),
('恢复或回放不可用','检查快照是否属于当前数据目录；独立重算只接受完整日志，恢复也不会自动重建设备连接。'),
('设备或浏览器媒体不可用','核对驱动、协议与单位；浏览器检查本机地址、权限、对应实验输入/输出和页面报错。不要把缺设备当作已采集。')],[.34,.66])
section('一轮试用检查')
para('① 能启动并打开页面；② 公式 2→4、3→9；③ 开始 / 暂停 / 停止状态正确；④ 保存快照、清空后恢复；⑤ 导出并核对数值；⑥ 正常停止服务后再移动完整文件夹测试。每项分别记录“通过 / 失败 / 未测试”。',10.5,17)
section('反馈时请提供')
para('Windows 版本和系统架构；本次软件包名称；所用实验文件；具体操作步骤；预期结果与实际结果；页面 / 终端报错截图。涉及设备时，再补型号、固件、驱动与协议。发送配置前隐藏密码或其他敏感信息。')
para('历史 117 组数值向量及既有功能记录本轮未重跑。本轮 Windows 11 ARM / x64 仿真 Edge 增量检查通过：1920 三列、2560 四列、无横溢、滚轮无 passive 错误、媒体页无自动采集或音频启动。真实媒体仍未验收；Windows 10、x64 实机、BLE / USB、U 盘长稳及完整性能待验收。',9.3,15,MUTED)
para('更多细节在程序包的 README.md、docs/VALIDATION.md、docs/COMPATIBILITY.md。当前供内部预览；完整功能和正式发布许可审查仍在进行。',9.3,15,MUTED)
endpage()
c.save()
import json
OUT.with_suffix('.layout-check.json').write_text(json.dumps(checks,indent=2))
print(OUT)
print(checks)
