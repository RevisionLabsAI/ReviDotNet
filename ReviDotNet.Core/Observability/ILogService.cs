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

// This file is bleh, please ignore how bleh this file is. Thank you. :) 


public interface ILogService
{
	public Record LogInfo(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogInfo(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogDebug(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogDebug(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogWarning(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogWarning(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogError(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogError(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogFatal(
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Record LogFatal(
		Record parent,
		string message,
		string? identifier = "",
		int cycle = 0,
		string? tags = null,
		object? object1 = null,
		object? object2 = null,
		[CallerFilePath] string? file = "",
		[CallerMemberName] string? member = "",
		[CallerLineNumber] int? line = 0);

	public Task DumpLog(StringBuilder sb, string fileNamePrefix);
	public Task DumpLog(string? textToDump, string fileNamePrefix);

	/// <summary>
	/// Dump binary image bytes to the same location/pattern as DumpLog, using provided prefix and image extension.
	/// </summary>
	/// <param name="imageBytes">Binary image content (e.g., PNG bytes)</param>
	/// <param name="fileNamePrefix">Prefix for the dumped image file</param>
	/// <param name="extension">Image extension without dot (defaults to "png")</param>
	public Task DumpImage(byte[] imageBytes, string fileNamePrefix, string extension = "png");
}