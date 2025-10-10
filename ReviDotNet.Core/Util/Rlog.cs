// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

using System.Diagnostics;
using System.Net.Mime;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.DeepDev;

namespace Revi;


public class Rlog
{
	public string Id;
	public Rlog? Parent;
	public string Identifier; // "Begin Loop" becomes "begin-loop"
	public string Message;
	public int Cycle;
	public List<string> Tags;
	
	public object Object1;
	public object Object2;

	public StringBuilder? Builder;
	
	// Constructor
	public Rlog(
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string tags = "",
		object? object1 = null,
		object? object2 = null,
		string? file = "",
		string? member = "",
		int? line = 0)
	{
		// Probably won't be a standard UUID but this is TBD
		Id = Guid.NewGuid().ToString(); 
		
		// Nullable property is allowed to be null
		Parent = null; 
		
		// "Begin Loop" becomes "begin-loop"
		Identifier = identifier.ToLower().Replace(" ", "-");  
		
		Message = message;
		
		// Cycle parameter is optional, if left null then cycle "0" is set
		Cycle = cycle; 
		
		// Process tags to a list of individual strings
		//Tags = tags; // TODO: Make it lowercase, trim empty space, etc
		
		
		
		//SendLogEventAsync(ID, Parent, OperationName, Message, Cycle, Tags);
	}
	
	// Constructor
	public Rlog(
		Rlog? parent,
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string tags = "",
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		// Probably won't be a standard UUID but this is TBD
		Id = Guid.NewGuid().ToString(); 
		
		// Nullable property is allowed to be null
		Parent = parent; 
		
		// "Begin Loop" becomes "begin-loop"
		Identifier = identifier.ToLower().Replace(" ", "-");  
		
		Message = message;
		
		// Cycle parameter is optional, if left null then cycle "0" is set
		Cycle = cycle; 
		
		// Process tags to a list of individual strings
		//Tags = tags; // TODO: Make it lowercase, trim empty space, etc
		
		
		
		//SendLogEventAsync(ID, Parent, OperationName, Message, Cycle, Tags);
	}
	
	

	public static void Test()
	{
		Rlog rootNode = new("message");
	}
	
	public static void LogBuild(StringBuilder sb, string text)
	{
		sb.Append(text);
		Log(text);
	}

	public override string ToString()
	{
		if (Builder != null)
			return Builder.ToString();

		return Message;
	}
	
	public static void Log(string text,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0)
	{
		bool debug = false;

		lock (LogLock)
		{
			if (debug)
			{
				var message = string.Format("{0}:{1}:{2}: ", 
					Path.GetFileName(file), 
					member, 
					line);
				var originalColor = Console.ForegroundColor;
				Console.ForegroundColor = ConsoleColor.DarkGray;
				Console.Write(message);
				Console.ForegroundColor = originalColor;
				Console.Write($"{text.Replace("\n", "\n" + message)}\n");
			}
			else
			{
				Console.WriteLine(text.Replace("\\n", "\n"));
			}
		}
	}
}