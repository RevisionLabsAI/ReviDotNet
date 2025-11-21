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
using MongoDB.Bson;

namespace Revi;

public class Rlog
{
	// Auto-generated
	public readonly string Id;
	public readonly DateTime Timestamp;
	
	public readonly Rlog? Parent;
	public readonly LogLevel Level;
	public readonly string Identifier; // "Begin Loop" becomes "begin-loop"
	public readonly string Message;
	public readonly int Cycle;
	public readonly string[]? Tags;
	
	public readonly object? Object1;
	public readonly string? Object1Name;
	public readonly object? Object2;
	public readonly string? Object2Name;
	
	public readonly string? File;
	public readonly string? Member;
	public readonly int? Line;

	public readonly StringBuilder? Builder;
	

	// Constructor
	public Rlog(
		Rlog? parent,
		LogLevel level,
		string message, 
		string? identifier = "", 
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		string? object1Name = null,
		object? object2 = null,
		string? object2Name = null,
		string? file = null,
		string? member = null,
		int? line = null)
	{
		// Generate MongoDB ObjectId compatible string
		Id = ObjectId.GenerateNewId().ToString();
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
		// Accept both spaces and commas as separators
		Tags = string.IsNullOrEmpty(tags)
			? []
			: tags.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
		
		// Normalize all tags: lowercase and trim
		for (int i = 0; i < Tags.Length; i++)
		{
			Tags[i] = Tags[i].ToLower().Trim();
		}
		
		Object1 = object1;
		Object1Name = object1Name;
		Object2 = object2;
		Object2Name = object2Name;
		
		File = file;
		Member = member;
		Line = line;
		
		Builder = new StringBuilder();
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