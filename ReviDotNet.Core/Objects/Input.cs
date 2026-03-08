// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

public class Input(string label, string text)
{
    public readonly string Identifier = Util.Identifierize(label);
    public readonly string Label = label;
    public readonly string Text = text;
}
