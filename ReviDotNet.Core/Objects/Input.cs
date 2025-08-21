namespace Revi;

public class Input(string label, string text)
{
    public readonly string Identifier = Util.Identifierize(label);
    public readonly string Label = label;
    public readonly string Text = text;
}
