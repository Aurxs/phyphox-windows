// SPDX-License-Identifier: GPL-3.0-only
#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Phyphox.Server;

internal sealed class DesktopWindow : Form
{
    readonly string[] arguments;
    readonly CancellationTokenSource stopping = new();
    readonly Label status = new() { Text = "正在启动本地服务…", AutoSize = true };
    readonly TextBox address = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly TextBox details = new() { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    readonly Button open = new() { Text = "打开实验页面", AutoSize = true, Enabled = false };
    readonly Button exit = new() { Text = "停止服务并退出", AutoSize = true };
    bool finished;
    bool closing;
    string? url;

    public DesktopWindow(string[] args)
    {
        arguments = args;
        Text = "phyphox 实验工作台";
        Icon = SystemIcons.Application;
        ClientSize = new Size(560, 260);
        MinimumSize = new Size(480, 280);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(open);
        buttons.Controls.Add(exit);
        layout.Controls.Add(status, 0, 0);
        layout.Controls.Add(address, 0, 1);
        layout.Controls.Add(details, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        open.Click += (_, _) => OpenBrowser();
        exit.Click += (_, _) => Close();
        Shown += StartService;
        FormClosing += (_, e) =>
        {
            if (finished) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            open.Enabled = exit.Enabled = false;
            status.Text = "正在停止服务并保存数据…";
            stopping.Cancel();
        };
    }

    async void StartService(object? sender, EventArgs e)
    {
        try
        {
            await Task.Run(() => Program.RunServerAsync(arguments, (endpoint, dataRoot) =>
                BeginInvoke(new Action(() =>
                {
                    if (closing) return;
                    url = endpoint;
                    address.Text = endpoint;
                    status.Text = "本地服务运行中";
                    details.Text = $"数据目录：{dataRoot}\r\n\r\n可最小化此窗口，后端会继续运行。\r\n关闭浏览器不会停止服务；关闭此窗口会停止服务。";
                    open.Enabled = true;
                    if (!arguments.Contains("--no-browser")) OpenBrowser();
                })), stopping.Token));
            finished = true;
            Close();
        }
        catch (OperationCanceledException) when (closing)
        {
            finished = true;
            Close();
        }
        catch (Exception error)
        {
            finished = true;
            open.Enabled = false;
            exit.Enabled = true;
            status.Text = "服务未能运行";
            details.Text = error.Message + "\r\n\r\n请保留完整程序文件夹，并确认未重复运行、数据目录可写。";
            if (closing) Close();
        }
    }

    void OpenBrowser()
    {
        if (url is null) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { details.Text = $"无法自动打开浏览器：{error.Message}\r\n请复制上方地址到浏览器打开。"; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) stopping.Dispose();
        base.Dispose(disposing);
    }
}
#endif
