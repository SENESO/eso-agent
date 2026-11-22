using System.Windows.Forms;

namespace EsoAgent.Tray;

/// <summary>
/// The in-tray audit-log viewer (UI Phase 5, gap G18): "View log" used to
/// shell out to Notepad — an external program, no refresh, no follow. This
/// window reads the log itself: the tail (last ~256 KB, so a huge log
/// never stalls the open), a Refresh button, and a Follow mode that
/// appends new lines on a 2 s timer while the window is open.
/// </summary>
public sealed class LogViewerForm : Form
{
    private readonly string _path;
    private readonly TextBox _box = new();
    private readonly CheckBox _followBox = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private long _seenLength = -1;

    public LogViewerForm(string path)
    {
        _path = path;
        Text = TrayStrings.LogTitle;
        Width = 860;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = TrayStrings.IsArabic ? RightToLeft.Yes : RightToLeft.No;

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        var refresh = new Button { Text = TrayStrings.Refresh, AutoSize = true };
        refresh.Click += (_, _) => LoadTail();
        _followBox.Text = TrayStrings.Follow;
        _followBox.AutoSize = true;
        _followBox.Checked = true;
        _followBox.Margin = new Padding(10, 6, 0, 0);
        bar.Controls.Add(refresh);
        bar.Controls.Add(_followBox);
        Controls.Add(bar);

        _box.Dock = DockStyle.Fill;
        _box.Multiline = true;
        _box.ReadOnly = true;
        _box.ScrollBars = ScrollBars.Vertical;
        _box.Font = new Font("Consolas", 9);
        _box.WordWrap = false;
        Controls.Add(_box);
        _box.BringToFront();

        _timer.Tick += (_, _) => FollowTick();
        Shown += (_, _) => { LoadTail(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void LoadTail()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) { _box.Text = ""; _seenLength = 0; return; }
            _seenLength = info.Length;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tail = (int)Math.Min(fs.Length, 256 * 1024);
            fs.Seek(-tail, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            if (tail < fs.Length)
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..]; // drop the partial first line
            }
            _box.Text = text;
            ScrollToEnd();
        }
        catch { /* the log may rotate mid-read; the next tick retries */ }
    }

    private void FollowTick()
    {
        if (!_followBox.Checked) return;
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return;
            if (info.Length == _seenLength) return;
            if (info.Length < _seenLength) { LoadTail(); return; } // rotated/truncated
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_seenLength, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var added = reader.ReadToEnd();
            _seenLength = info.Length;
            if (added.Length == 0) return;
            _box.AppendText(added);
            ScrollToEnd();
        }
        catch { /* next tick retries */ }
    }

    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }
}
