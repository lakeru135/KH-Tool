using System.Windows.Forms;
using KhBatchRunner.Batch;

namespace KhBatchRunner.UI;

/// <summary>
/// The "Coordinates:" dropdown shared by Export NWC and Export IFC + Data, with the explanation
/// under it that changes with the choice. One builder so both dialogs offer the same three choices
/// in the same words (<see cref="ExportCoordinatesText"/>), and the "Project Base Point at 0,0,0"
/// explanation — what it does to the model — is in front of the user when they pick it rather than
/// only in the handbook.
/// </summary>
internal sealed class CoordinatesPicker
{
    private static readonly ExportCoordinates[] Order =
        { ExportCoordinates.Shared, ExportCoordinates.Internal, ExportCoordinates.ProjectBasePoint };

    private readonly bool _isNwc;

    public ComboBox Combo { get; }
    public Label Note { get; }

    /// <param name="host">The dialog, so the note wraps to its width.</param>
    /// <param name="isNwc">Selects the NWC or the IFC wording of the PBP explanation — the two
    /// exports reach that frame differently.</param>
    public CoordinatesPicker(Form host, bool isNwc)
    {
        _isNwc = isNwc;
        Combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        foreach (ExportCoordinates c in Order)
            Combo.Items.Add(new Item(c));
        Note = FormLayout.NoteLabel(host, "");
        Combo.SelectedIndexChanged += (_, _) =>
        {
            Note.Text = ExportCoordinatesText.Explanation(Value, _isNwc);
            NoteChanged?.Invoke();
        };
        Combo.SelectedIndex = 0;
    }

    /// <summary>
    /// Raised after the note's text changes. The Project Base Point explanation is several lines
    /// longer than the others, so the dialog must refit (<see cref="FormLayout.KeepFitted"/>) or
    /// its rows overflow — the 2.1.0.8 failure.
    /// </summary>
    public event Action? NoteChanged;

    public ExportCoordinates Value
    {
        get => Combo.SelectedItem is Item i ? i.Value : ExportCoordinates.Shared;
        set
        {
            for (int n = 0; n < Combo.Items.Count; n++)
                if (((Item)Combo.Items[n]).Value == value) { Combo.SelectedIndex = n; return; }
        }
    }

    private sealed class Item
    {
        public ExportCoordinates Value { get; }
        public Item(ExportCoordinates value) => Value = value;
        public override string ToString() => ExportCoordinatesText.Label(Value);
    }
}
