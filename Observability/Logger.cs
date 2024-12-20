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
 
    /*
         private static LogNode Log(
           LogNode? parent, 
           string? identifier, 
           string? message = null,
           string? tags = null,
           int? cycle = null,
           object? o1 = null, 
           object? o2 = null)
       {
           return Logger.LogNode(
               parent, 
               identifier, 
               message,
               tags,
               cycle,
               o1, 
               o2);
       }
       //Log(null, "foobar", "log message", o1: tlds);
     */
}