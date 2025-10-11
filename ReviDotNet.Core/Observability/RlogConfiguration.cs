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

namespace Revi.Configuration;

/// <summary>
/// Configuration model for ReviLogger settings
/// </summary>
public class RlogConfiguration
{
    public RlogLevelConfiguration Debug { get; set; } = new() { ConsolePrint = false };
    public RlogLevelConfiguration Info { get; set; } = new() { ConsolePrint = true };
    public RlogLevelConfiguration Warning { get; set; } = new() { ConsolePrint = true };
    public RlogLevelConfiguration Error { get; set; } = new() { ConsolePrint = true };
    public RlogLevelConfiguration Fatal { get; set; } = new() { ConsolePrint = true };
}

/// <summary>
/// Configuration model for individual log level settings
/// </summary>
public class RlogLevelConfiguration
{
    public string PrefixColor { get; set; } = "Gray";
    public string TextColor { get; set; } = "Gray";
    public bool ConsolePrint { get; set; } = true;
}