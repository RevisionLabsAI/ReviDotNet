// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;

namespace Revi;

public static partial class Util
{
	// Sensitive URL query parameters whose value must never reach a log sink. The Gemini protocol
	// passes the API key as ?key=<API_KEY>, so at minimum "key" must be covered; the rest are common
	// secret-bearing parameter names. [?&] anchors the name to a real parameter boundary so we don't
	// match "key" inside an unrelated word, and the value runs until the next delimiter (&, #, quote,
	// whitespace) so we strip the whole secret but keep the rest of the URL intact for debugging.
	private static readonly Regex SecretQueryParamRegex = new Regex(
		"(?<prefix>[?&](?:key|api[_-]?key|access[_-]?token|auth[_-]?token|token|password|secret)=)(?<value>[^&#\\s\"'<>]+)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Authorization / x-api-key style header values, in case a header dump is ever logged. The optional
	// "Bearer " is consumed (not captured) so it is dropped along with the token rather than left behind.
	private static readonly Regex SecretHeaderRegex = new Regex(
		"(?<prefix>(?:authorization|x-api-key|api-key|x-goog-api-key)\\s*[:=]\\s*)(?:Bearer\\s+)?(?<value>[^\\s,;\"'<>]+)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>
	/// Redacts secrets from a string before it is written to any log sink (console, ReviLog, Mongo, …).
	/// Masks the value of sensitive URL query parameters (e.g. <c>?key=SECRET</c> becomes
	/// <c>?key=***</c>) and Authorization / x-api-key style header values, while leaving the rest of the
	/// text intact for debuggability. Always call this on a request URL (or any message that embeds one)
	/// before logging it.
	/// </summary>
	/// <param name="text">A URL, or any log message that may embed a URL or header value.</param>
	/// <returns>The text with secret values replaced by <c>***</c>.</returns>
	public static string RedactSecrets(string? text)
	{
		if (string.IsNullOrEmpty(text))
			return text ?? string.Empty;

		string result = SecretQueryParamRegex.Replace(text, m => m.Groups["prefix"].Value + "***");
		result = SecretHeaderRegex.Replace(result, m => m.Groups["prefix"].Value + "***");
		return result;
	}
}
