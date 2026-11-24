using FastTaskMgr.Core.Native;
using static FastTaskMgr.UI.UiScale;

namespace FastTaskMgr.UI.Dialogs;

internal sealed class AffinityDialog : Form
{
    private readonly List<(int Cpu, CheckBox Box)> _cpuBoxes = [];
    private readonly CheckBox _allBox;
    private readonly int _processId;
    private bool _updatingAll;

    private AffinityDialog(int processId, string processName, ulong processMask, ulong systemMask)
    {
        _processId = processId;
        Text = $"Processor affinity - {processName} ({processId})";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;

        FlowLayoutPanel cpuPanel = new()
        {
            FlowDirection = FlowDirection.TopDown,
            Location = new Point(Px(12), Px(44)),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            MaximumSize = new Size(0, Px(320))
        };

        _allBox = new CheckBox { Text = "<All processors>", AutoSize = true, Location = new Point(Px(12), Px(12)) };
        _allBox.CheckedChanged += (_, _) => SetAll(_allBox.Checked);
        Controls.Add(_allBox);

        for (int cpu = 0; cpu < 64; cpu++)
        {
            if ((systemMask & (1UL << cpu)) == 0)
            {
                continue;
            }

            CheckBox box = new()
            {
                Text = $"CPU {cpu}",
                AutoSize = true,
                Checked = (processMask & (1UL << cpu)) != 0
            };
            box.CheckedChanged += (_, _) => SyncAllBox();
            _cpuBoxes.Add((cpu, box));
            cpuPanel.Controls.Add(box);
        }

        Controls.Add(cpuPanel);
        SyncAllBox();

        Button ok = new() { Text = "OK", DialogResult = DialogResult.None, Width = Px(88) };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = Px(88) };
        ok.Click += (_, _) => Apply();
        FlowLayoutPanel buttons = new()
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = Px(44),
            Padding = Px(new Padding(8))
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        int columns = Math.Max(1, (_cpuBoxes.Count + 15) / 16);
        ClientSize = new Size(Math.Max(Px(280), Px(24 + columns * 110)), Math.Min(Px(320), Px(24 + Math.Min(16, _cpuBoxes.Count) * 24)) + Px(88));
    }

    /// <summary>Returns true when a new affinity was applied.</summary>
    public static bool Show(IWin32Window owner, int processId, string processName)
    {
        if (!NativeMethods.QueryAffinityMasks(processId, out ulong processMask, out ulong systemMask) || systemMask == 0)
        {
            MessageBox.Show(owner, "Could not read the processor affinity. Access may be denied.", "Processor affinity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        using AffinityDialog dialog = new(processId, processName, processMask, systemMask);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private void SetAll(bool value)
    {
        if (_updatingAll)
        {
            return;
        }

        _updatingAll = true;
        foreach ((_, CheckBox box) in _cpuBoxes)
        {
            box.Checked = value;
        }

        _updatingAll = false;
    }

    private void SyncAllBox()
    {
        if (_updatingAll)
        {
            return;
        }

        _updatingAll = true;
        _allBox.Checked = _cpuBoxes.All(pair => pair.Box.Checked);
        _updatingAll = false;
    }

    private void Apply()
    {
        ulong mask = 0;
        foreach ((int cpu, CheckBox box) in _cpuBoxes)
        {
            if (box.Checked)
            {
                mask |= 1UL << cpu;
            }
        }

        if (mask == 0)
        {
            MessageBox.Show(this, "Select at least one processor.", "Processor affinity", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            NativeMethods.SetAffinity(_processId, mask);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Processor affinity", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
