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

public class Record
{
	// Auto-generated
	public string Id;
	public DateTime Timestamp;
	
	public Record? Parent;
	public LogLevel Level;
	public string Identifier; // "Begin Loop" becomes "begin-loop"
	public string Message;
	public int Cycle;
	public string[]? Tags;
	
	public object Object1;
	public object Object2;

	public StringBuilder? Builder;
	
	// Constructor
	public Record(
		Record? parent,
		LogLevel level,
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string tags = null,
		object? object1 = null,
		object? object2 = null,
		string? file = "",
		string? member = "",
		int? line = 0)
	{
		// Probably won't be a standard UUID but this is TBD
		Id = Guid.NewGuid().ToString(); 
		Timestamp = DateTime.Now;
		
		// Nullable property is allowed to be null
		Parent = parent;

		Level = level;
		
		if (string.IsNullOrEmpty(identifier))
		{
			if (!string.IsNullOrEmpty(member))
				Identifier = member;
			else
				Identifier = level.ToString();
		}
		else
		{
			Identifier = identifier;
		}
		
		// "Begin Loop" becomes "begin-loop"
		Identifier = Identifier.ToLower().Replace(" ", "-");
		
		Message = message;
		
		// Cycle parameter is optional, if left null then cycle "0" is set
		Cycle = cycle; 
		
		// Process tags to a list of individual strings
		Tags = tags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		
		// Convert all tags to lowercase
		for (int i = 0; i < Tags.Length; i++)
		{
			Tags[i] = Tags[i].ToLower().Trim();
		}
	}

	public async Task Dump(string? fileNamePrefix = null)
	{
		string dumpText;
		
		if (string.IsNullOrEmpty(fileNamePrefix))
			fileNamePrefix = Identifier;
		
		if (Builder != null)
			dumpText = Builder.ToString();
		else
			dumpText = Message;
		
		await Util.DumpLog(dumpText, fileNamePrefix);
	}
	
	public override string ToString()
	{
		if (Builder != null)
			return Builder.ToString();

		return Message;
	}
}