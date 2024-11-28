namespace Revi;

public class Input(string label, string text)
{
    public readonly string Identifier = RUtil.Identifierize(label);
    public string Label = label;
    public string Text = text;
}
