using System.Globalization;

namespace KhBatchRunner.UI;

/// <summary>
/// A time-of-day input for the schedule windows. A <see cref="DateTimePicker"/> with an up/down
/// spinner, formatted with the PC's own SHORT time pattern (12-hour <c>h:mm tt</c> or 24-hour
/// <c>HH:mm</c>, per the Windows Region settings — no seconds) rather than a text box the user has
/// to type a parseable time into. The stored value stays canonical <c>HH:mm</c> so a schedule's
/// <c>Time</c> means the same thing on any machine, whatever it is displayed as here.
/// </summary>
internal static class TimeInput
{
    /// <summary>A time picker defaulting to now + 5 minutes (a safely future trigger).</summary>
    public static DateTimePicker Create() => new()
    {
        // Custom + the PC's short time pattern shows 12h AM/PM or 24h to match the taskbar, without
        // the seconds that DateTimePickerFormat.Time adds; the wider box fits "12:00 AM" + spinner.
        Format = DateTimePickerFormat.Custom,
        CustomFormat = ScheduleDisplay.TimePattern,
        ShowUpDown = true,
        Width = 130,
        Value = DateTime.Now.AddMinutes(5)
    };

    /// <summary>
    /// Canonical <c>HH:mm</c> for storage, independent of display locale. A picker cannot hold an
    /// unparseable value, so this always succeeds — the Try shape is kept because the four save
    /// handlers are written against it and a future editor may well be able to fail.
    /// </summary>
    public static bool TryStored(DateTimePicker picker, out string stored)
    {
        stored = picker.Value.ToString(ScheduleDisplay.StoredTimePattern, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Sets the picker from a stored <c>HH:mm</c>; falls back to now + 5 minutes if unparseable.</summary>
    public static void SetStored(DateTimePicker picker, string? hhmm)
    {
        picker.Value = ScheduleDisplay.TryStoredTime(hhmm, out TimeSpan time)
            ? DateTime.Today.Add(time)
            : DateTime.Now.AddMinutes(5);
    }
}

/// <summary>
/// A calendar-date input for a <see cref="Schedule.ScheduleRecurrence.Once"/> job's start date. A
/// <see cref="DateTimePicker"/> with its dropdown calendar, formatted with the PC's short-date
/// pattern; the stored value is the date part only (the time comes from <see cref="TimeInput"/>).
/// </summary>
internal static class DateInput
{
    public static DateTimePicker Create() => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = ScheduleDisplay.DatePattern,
        Width = 130,
        Value = DateTime.Today
    };

    /// <summary>The picked calendar date. Always succeeds — see <see cref="TimeInput.TryStored"/>.</summary>
    public static bool TryRead(DateTimePicker picker, out DateTime date)
    {
        date = picker.Value.Date;
        return true;
    }

    /// <summary>Shows a stored start date. Clamped because <see cref="DateTimePicker.Value"/>
    /// THROWS outside its Min/Max (1753..9998), and a job whose JSON holds a default or garbage
    /// date would otherwise take the whole schedule window down instead of just looking wrong.</summary>
    public static void SetDate(DateTimePicker picker, DateTime date)
    {
        DateTime d = date.Date;
        if (d < picker.MinDate) d = picker.MinDate;
        else if (d > picker.MaxDate) d = picker.MaxDate;
        picker.Value = d;
    }
}
