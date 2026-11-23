using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using DotNet.Yarp.Reverse.Proxy.Gateway.Core;

namespace NginxConfigConverter.UI;

/// <summary>
/// nginx.conf → YARP 配置转换工具（WinForms 界面）。
/// 转换逻辑直接复用 Core 类库的 NginxConfigConverter（源码链接，纯 BCL）。
/// 提供：文件/粘贴双模式输入、JSON 结果预览、迁移提示清单、复制/保存、备份并合并到 appsettings.json。
/// </summary>
internal sealed class MainForm : Form
{
    private readonly RadioButton _rdoFile = new() { Text = "文件", Checked = true };
    private readonly RadioButton _rdoPaste = new() { Text = "粘贴文本" };
    private readonly TextBox _txtFilePath = new() { ReadOnly = true };
    private readonly Button _btnBrowse = new() { Text = "浏览..." };
    private readonly TextBox _txtPaste = new() { Multiline = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), AcceptsReturn = true };

    private readonly TextBox _txtResult = new() { Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f), WordWrap = false };

    private readonly ListBox _lstNotes = new() { Dock = DockStyle.Fill,
        Font = new Font("Microsoft YaHei UI", 9f), HorizontalScrollbar = true };

    private readonly Label _lblStatus = new() { Dock = DockStyle.Bottom, Height = 26,
        Text = "就绪", Padding = new Padding(6, 4, 6, 4), BorderStyle = BorderStyle.FixedSingle };

    private NginxConversionResult? _lastResult;
    private string _sourceConf = string.Empty;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MainForm()
    {
        Text = "nginx.conf 转换工具  →  YARP 网关配置";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1120, 760);

        BuildUi();
    }

    private void BuildUi()
    {
        // ---------- 顶部：输入模式 ----------
        var inputMode = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 0) };
        var modeLabel = new Label { Text = "输入方式：", AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
        _rdoFile.AutoSize = true;
        _rdoPaste.AutoSize = true;
        _rdoFile.CheckedChanged += (_, _) => SwitchInputMode();
        inputMode.Controls.AddRange(new Control[] { modeLabel, _rdoFile, _rdoPaste });

        // 文件输入行
        var fileRow = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 4, 8, 4) };
        _txtFilePath.Dock = DockStyle.Left;
        _txtFilePath.Width = 820;
        _btnBrowse.Dock = DockStyle.Left;
        _btnBrowse.Width = 90;
        _btnBrowse.Click += (_, _) => BrowseFile();
        fileRow.Controls.Add(_btnBrowse);
        fileRow.Controls.Add(_txtFilePath);

        // 粘贴输入（默认隐藏）
        var pastePanel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8, 0, 8, 8) };
        pastePanel.Controls.Add(_txtPaste);

        // 输入容器
        var inputGroup = new GroupBox { Text = "① 输入 nginx.conf", Dock = DockStyle.Top,
            Height = 300, Padding = new Padding(8, 8, 8, 8) };
        // 用 TableLayoutPanel 让文件行/粘贴区切换
        var inputBody = new Panel { Dock = DockStyle.Fill };
        inputBody.Controls.Add(pastePanel);
        inputBody.Controls.Add(fileRow);
        inputGroup.Controls.Add(inputBody);
        inputGroup.Controls.Add(inputMode);
        // 调整层级：inputMode 在最上（后加的 Dock=Top 会在下方，这里用 BringToFront）
        inputMode.BringToFront();

        // ---------- 操作按钮行 ----------
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 6, 8, 4) };
        var btnConvert = new Button { Text = "转换", Width = 96, Height = 34 };
        btnConvert.Click += (_, _) => DoConvert();
        var btnClear = new Button { Text = "清空", Width = 80, Height = 34 };
        btnClear.Click += (_, _) => ClearAll();
        var btnCopy = new Button { Text = "复制 JSON", Width = 100, Height = 34 };
        btnCopy.Click += (_, _) => CopyResult();
        var btnSave = new Button { Text = "保存 JSON", Width = 100, Height = 34 };
        btnSave.Click += (_, _) => SaveResult();
        var btnMerge = new Button { Text = "备份并合并到 appsettings.json", Width = 240, Height = 34 };
        btnMerge.Click += (_, _) => MergeIntoAppsettings();
        actions.Controls.AddRange(new Control[] { btnConvert, btnClear, btnCopy, btnSave, btnMerge });

        // ---------- 中部：结果 + 提示 ----------
        var resultGroup = new GroupBox { Text = "② 转换结果（YARP ReverseProxy 配置）",
            Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 8) };
        resultGroup.Controls.Add(_txtResult);

        var notesGroup = new GroupBox { Text = "③ 迁移提示（需人工复核）", Dock = DockStyle.Right,
            Width = 380, Padding = new Padding(8, 8, 8, 8) };
        notesGroup.Controls.Add(_lstNotes);

        var middle = new Panel { Dock = DockStyle.Fill };
        // 先加 result（Fill），再加 notes（Right）使其停靠右侧
        middle.Controls.Add(resultGroup);
        middle.Controls.Add(notesGroup);

        // ---------- 组装（注意 Dock 顺序：后添加的先停靠，Fill 最后）----------
        Controls.Add(_lblStatus);       // Bottom
        Controls.Add(middle);           // Fill
        Controls.Add(actions);          // Top
        Controls.Add(inputGroup);       // Top（最上）
    }

    private void SwitchInputMode()
    {
        var fileRow = _txtFilePath.Parent!;
        var pastePanel = _txtPaste.Parent!;
        fileRow.Visible = _rdoFile.Checked;
        pastePanel.Visible = _rdoPaste.Checked;
    }

    private void BrowseFile()
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "nginx 配置文件 (*.conf;*.txt)|*.conf;*.txt|所有文件 (*.*)|*.*",
            Title = "选择 nginx.conf"
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
            _txtFilePath.Text = ofd.FileName;
    }

    private void DoConvert()
    {
        try
        {
            var path = _txtFilePath.Text.Trim();
            if (_rdoFile.Checked)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    SetStatus("请先选择一个有效的 nginx.conf 文件。", true);
                    return;
                }
                _sourceConf = File.ReadAllText(path);
            }
            else
            {
                _sourceConf = _txtPaste.Text;
            }

            if (string.IsNullOrWhiteSpace(_sourceConf))
            {
                SetStatus("输入内容为空。", true);
                return;
            }

            var sw = Stopwatch.StartNew();
            // 从文件转换时传入所在目录，include 才能按各自文件位置递归展开；
            // 粘贴模式没有基准目录，转换器会给出"未展开 include"的提示而不是静默丢配置。
            var baseDir = _rdoFile.Checked ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
            var result = DotNet.Yarp.Reverse.Proxy.Gateway.Core.NginxConfigConverter.Convert(_sourceConf, baseDir);
            sw.Stop();
            _lastResult = result;

            _txtResult.Text = result.Json;
            _lstNotes.Items.Clear();
            foreach (var note in result.Notes) _lstNotes.Items.Add(note);

            SetStatus($"转换完成：{result.RouteCount} 个路由，{result.ClusterCount} 个集群，{result.Notes.Count} 条提示（耗时 {sw.ElapsedMilliseconds} ms）。");
        }
        catch (Exception ex)
        {
            SetStatus("转换失败：" + ex.Message, true);
            MessageBox.Show(this, ex.Message, "转换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ClearAll()
    {
        _sourceConf = string.Empty;
        _lastResult = null;
        _txtFilePath.Clear();
        _txtPaste.Clear();
        _txtResult.Clear();
        _lstNotes.Items.Clear();
        SetStatus("已清空。");
    }

    private void CopyResult()
    {
        if (_lastResult is null) { SetStatus("请先执行转换。", true); return; }
        Clipboard.SetText(_lastResult.Json);
        SetStatus("JSON 已复制到剪贴板。");
    }

    private void SaveResult()
    {
        if (_lastResult is null) { SetStatus("请先执行转换。", true); return; }
        using var sfd = new SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            FileName = "converted-appsettings.json",
            Title = "保存转换结果"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(sfd.FileName, _lastResult.Json, new UTF8Encoding(false));
            SetStatus("已保存到：" + sfd.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
        }
    }

    private void MergeIntoAppsettings()
    {
        if (_lastResult is null) { SetStatus("请先执行转换。", true); return; }

        using var ofd = new OpenFileDialog
        {
            Filter = "appsettings 配置文件 (appsettings*.json)|appsettings*.json|JSON 文件 (*.json)|*.json",
            Title = "选择要合并到的 appsettings.json"
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        var target = ofd.FileName;
        var confirm = MessageBox.Show(this,
            $"将用转换结果的 ReverseProxy 节点【替换】以下文件的 ReverseProxy 节点：\n\n{target}\n\n" +
            "原文件会先备份为 .bak.<时间戳>，Gateway 等其他节点保持不变。\n\n确定继续？",
            "确认合并", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            // 备份原文件
            var backup = target + ".bak." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(target, backup, overwrite: false);

            // 读取原配置并替换 ReverseProxy 节点
            var root = JsonNode.Parse(File.ReadAllText(target)) as JsonObject
                       ?? throw new InvalidDataException("appsettings.json 根节点不是 JSON 对象。");
            var converted = JsonNode.Parse(_lastResult.Json) as JsonObject
                            ?? throw new InvalidDataException("转换结果格式异常。");
            var newRp = converted["ReverseProxy"]?.DeepClone()
                        ?? throw new InvalidDataException("转换结果缺少 ReverseProxy 节点。");
            root["ReverseProxy"] = newRp;

            File.WriteAllText(target, root.ToJsonString(JsonWriteOptions), new UTF8Encoding(false));
            SetStatus($"已合并并备份：{backup}");
            MessageBox.Show(this,
                $"合并完成！\n\n目标：{target}\n备份：{backup}\n\n请核对配置并用 --test-config 预检后重启服务。",
                "合并完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetStatus("合并失败：" + ex.Message, true);
            MessageBox.Show(this, "合并失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string text, bool isError = false)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = isError ? Color.DarkRed : SystemColors.ControlText;
    }
}
