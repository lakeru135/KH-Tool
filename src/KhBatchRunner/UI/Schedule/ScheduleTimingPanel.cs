using System.Drawing;
using System.Windows.Forms;
using KhBatchRunner.Schedule;

// Folder UI\Schedule\ groups the shared schedule panels; the NAMESPACE stays KhBatchRunner.UI —
// see ScheduleJobListPanel for why nesting it shadows KhBatchRunner.Schedule.
namespace KhBatchRunner.UI;

/// <summary>
/// "When does this job run" — name, recurrence, start date, time, and the weekly-day checkboxes.
///
/// <para><b>Why this is shared (1.4.0).</b> All four Schedule* windows built the same two rows by
/// hand and carried the same <c>UpdateWeeklyEnabled</c> and <c>DayFor</c>. The three near-identical
/// copies of <c>UpdateWeeklyEnabled</c> differed only in a local variable's name — but each was a
/// separate place for the next rule about dates to be applied three times out of four.</para>
///
/// <para>The layout comment worth keeping: eight controls on one line is wider than these dialogs
/// at their minimum, and a <see cref="FlowLayoutPanel"/> does not wrap by default — so "Time:" and
/// its picker were once drawn PAST the panel's right edge and simply not visible. Filling the width
/// gives the row something to wrap against, and then wrapping is allowed.</para>
/// </summary>
internal sealed class ScheduleTimingPanel : TableLayoutPanel
{
    private static readonly string[] DayLabels = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private readonly TextBox _nameBox;
    private readonly ComboBox _recurCombo;
    private readonly DateTimePicker _timePicker;
    private readonly DateTimePicker _datePicker;
    private readonly CheckBox[] _dayBoxes = new CheckBox[7];

    public ScheduleTimingPanel()
    {
        Label Lbl(string text) => new()
        {
            Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3)
        };

        _nameBox = new TextBox { Width = 240, Margin = new Padding(3, 3, 12, 3) };
        _recurCombo = new ComboBox
        {
            Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 12, 3)
        };
        foreach (ScheduleRecurrence r in Enum.GetValues(typeof(ScheduleRecurrence)))
            _recurCombo.Items.Add(new NamedItem(r.ToString(), r.ToString()));
        _recurCombo.SelectedIndex = 0;

        _timePicker = TimeInput.Create();
        // Start date only applies to a "Once" job — a fixed calendar date, so re-saving on a later
        // day no longer re-anchors it to that day (see TaskSchedulerGateway /SD).
        _datePicker = DateInput.Create();

        FlowLayoutPanel topFields = FormLayout.FlowRow(
            Lbl("Name:"), _nameBox, Lbl("Recurrence:"), _recurCombo,
            Lbl("Date:"), _datePicker, Lbl("Time:"), _timePicker);
        topFields.Dock = DockStyle.Fill;
        topFields.WrapContents = true;

        FlowLayoutPanel weekly = FormLayout.FlowRow(Lbl("Weekly days:"));
        for (int i = 0; i < 7; i++)
        {
            _dayBoxes[i] = new CheckBox
            {
                Text = DayLabels[i], AutoSize = true, Margin = new Padding(3, 4, 3, 3)
            };
            weekly.Controls.Add(_dayBoxes[i]);
        }

        _recurCombo.SelectedIndexChanged += (_, _) => UpdateEnabled();

        Dock = DockStyle.Fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 2;
        Margin = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(topFields, 0, 0);
        Controls.Add(weekly, 0, 1);

        UpdateEnabled();
    }

    public string ScheduleName
    {
        get => _nameBox.Text;
        set => _nameBox.Text = value;
    }

    public ScheduleRecurrence Recurrence =>
        (ScheduleRecurrence)Enum.Parse(typeof(ScheduleRecurrence),
            ((NamedItem)_recurCombo.SelectedItem!).Value);

    /// <summary>The stored "HH:mm". Both editors are DateTimePickers, so neither can hold a value
    /// that fails to read; the Try shape is kept so an editor that CAN fail would have somewhere to
    /// say so.</summary>
    public string Time
    {
        get { TimeInput.TryStored(_timePicker, out string time); return time; }
    }

    /// <summary>The fixed calendar date of a Once job; null for Daily and Weekly, which ignore it.</summary>
    public DateTime? StartDate
    {
        get
        {
            DateInput.TryRead(_datePicker, out DateTime picked);
            return Recurrence == ScheduleRecurrence.Once ? picked : null;
        }
    }

    public List<DayOfWeek> WeeklyDays =>
        _dayBoxes.Where(cb => cb.Checked).Select(cb => ScheduleRules.DayForLabel(cb.Text)).ToList();

    public void Load(ScheduledJob job)
    {
        _nameBox.Text = job.Name;
        SelectComboValue(_recurCombo, job.Recurrence.ToString());
        TimeInput.SetStored(_timePicker, job.Time);
        DateInput.SetDate(_datePicker, job.StartDate?.Date ?? DateTime.Today);
        foreach (CheckBox cb in _dayBoxes)
            cb.Checked = job.WeeklyDays.Contains(ScheduleRules.DayForLabel(cb.Text));
        UpdateEnabled();
    }

    /// <summary>Resets to the "New schedule" state.</summary>
    public void Clear()
    {
        _nameBox.Text = "";
        _recurCombo.SelectedIndex = 0;
        TimeInput.SetStored(_timePicker, null);
        DateInput.SetDate(_datePicker, DateTime.Today);
        foreach (CheckBox cb in _dayBoxes) cb.Checked = false;
        UpdateEnabled();
    }

    /// <summary>Day-of-week picks only apply to Weekly, and the start date only to Once. Greying the
    /// rest out is the only thing that says which fields the chosen recurrence actually reads.</summary>
    private void UpdateEnabled()
    {
        string? recur = (_recurCombo.SelectedItem as NamedItem)?.Value;
        bool weekly = recur == ScheduleRecurrence.Weekly.ToString();
        foreach (CheckBox cb in _dayBoxes) cb.Enabled = weekly;
        _datePicker.Enabled = recur == ScheduleRecurrence.Once.ToString();
    }

    private static void SelectComboValue(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
            if (((NamedItem)combo.Items[i]).Value == value) { combo.SelectedIndex = i; return; }
    }
}
