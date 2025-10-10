using System.Text;

namespace Revi;

public static class StringBuilderExtensions
{
    public static string GetLastLine(this StringBuilder sb)
    {
        if (sb.Length == 0) 
            return string.Empty;
        
        string text = sb.ToString();
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.LastOrDefault() ?? string.Empty;
    }
}