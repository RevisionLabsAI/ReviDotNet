namespace Revi;



public class LogNode
{
    public string UUID;
    public LogNode? Parent;
    public string Identifier; // "Begin Loop" becomes "begin-loop"
    public string Message;
    public List<string> Tags;
    public int Cycle;

    public object Object1;
    public object Object2;
	
    // Constructor
    public LogNode(
        LogNode? parent,
        string identifier, 
        string message, 
        string? tags = null, 
        int? cycle = null,
        object? o1 = null,
        object? o2 = null)
    {
        // Probably won't be a standard UUID but this is TBD
        UUID = "fsldkfj";//GenerateUUID; 
		
        // Nullable property is allowed to be null
        Parent = parent; 
		
        // "Begin Loop" becomes "begin-loop"
        //OperationName = operationName.ToLowercase.ReplaceSpacesWithDashes(); 
		
        Message = message;
		
        // Cycle parameter is optional, if left null then cycle "0" is set
        Cycle = cycle ?? 0; 
		
        // Process tags to a list of individual strings
        //Tags = tags.ToList; // TODO: Make it lowercase, trim empty space, etc
		
        //SendLogEventAsync(ID, Parent, OperationName, Message, Cycle, Tags);
    }
}