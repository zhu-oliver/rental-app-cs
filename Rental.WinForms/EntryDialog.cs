namespace Rental.WinForms;

internal record Choice(int Id, string Label)
{
    public override string ToString() => Label;
}

internal sealed class EntryDialog : Form
{
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
    private readonly ErrorProvider errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly Func<bool> save;

    public EntryDialog(string title, Func<bool> save)
    {
        this.save = save;
        Text = title; Width = 560; Height = 520; StartPosition = FormStartPosition.CenterParent;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var ok = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, CausesValidation = false };
        errors.ContainerControl = this;
        ok.Click += (_, _) => { try { if (ValidateChildren() && this.save()) { DialogResult = DialogResult.OK; Close(); } } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Please check your input", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(fields); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
    }

    private T Add<T>(string label, T control) where T : Control
    {
        int row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
        control.Dock = DockStyle.Top; control.Margin = new Padding(3, 3, 3, 8);
        fields.Controls.Add(control, 1, row);
        return control;
    }
    public TextBox TextField(string label, string value = "", bool secret = false, bool required = true, bool email = false)
    {
        var box = Add(label, new TextBox { Text = value, UseSystemPasswordChar = secret });
        box.Validating += (_, e) =>
        {
            string message = required && string.IsNullOrWhiteSpace(box.Text) ? $"{label} is required." :
                email && !System.Net.Mail.MailAddress.TryCreate(box.Text.Trim(), out var _) ? "Enter a valid email address." : "";
            errors.SetError(box, message);
            e.Cancel = message.Length > 0;
        };
        return box;
    }
    public ComboBox Select(string label, IEnumerable<Choice> choices, int? selected = null)
    {
        var box = Add(label, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        box.Items.AddRange(choices.Cast<object>().ToArray());
        if (selected is not null) box.SelectedItem = box.Items.Cast<Choice>().FirstOrDefault(c => c.Id == selected);
        else if (box.Items.Count > 0) box.SelectedIndex = 0;
        box.Validating += (_, e) =>
        {
            var message = box.SelectedItem is null ? $"Select {label.ToLowerInvariant()}." : "";
            errors.SetError(box, message);
            e.Cancel = message.Length > 0;
        };
        return box;
    }
    public DateTimePicker Date(string label, DateOnly? value = null, bool optional = false)
    {
        var picker = Add(label, new DateTimePicker { Format = DateTimePickerFormat.Short, ShowCheckBox = optional, Value = (value ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue) });
        if (optional) picker.Checked = value is not null;
        return picker;
    }
    public NumericUpDown Money(string label, long cents = 0) => Add(label, new NumericUpDown { DecimalPlaces = 2, Maximum = 1000000000, ThousandsSeparator = true, Value = cents / 100m });
    public static int Selected(ComboBox box) => (box.SelectedItem as Choice)?.Id ?? throw new ArgumentException("Select a value for every required choice.");
    public static DateOnly DateValue(DateTimePicker date) => DateOnly.FromDateTime(date.Value);
    public static DateOnly? OptionalDate(DateTimePicker date) => date.Checked ? DateValue(date) : null;
    protected override void Dispose(bool disposing)
    {
        if (disposing) errors.Dispose();
        base.Dispose(disposing);
    }
}
