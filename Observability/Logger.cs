namespace Revi;

public static class Logger
{
    public static LogNode LogNode(
        LogNode? parent, 
        string? identifier, 
        string? message = null, 
        string? tags = null,
        int? cycle = null,
        object? o1 = null, 
        object? o2 = null)
    {
        LogNode node = new(parent, identifier, message, tags, cycle, o1, o2);
        Console.WriteLine(message);
        return node;
    }
    
}