// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using MudBlazor;

namespace ReviDotNet.Forge.Theme;

/// <summary>
/// The Forge design system — a single shared <see cref="MudTheme"/> applied
/// app-wide via the <c>MudThemeProvider</c> in <c>MainLayout</c>.
///
/// The look is a contemporary developer-tool aesthetic: neutral slate
/// surfaces, a confident indigo accent, refined Inter typography and a
/// generous corner radius, leaning on subtle borders rather than heavy
/// Material shadows. Both light and dark palettes are tuned so the app
/// reads as professional in either mode.
/// </summary>
public static class ForgeTheme
{
    // Inter is the primary UI face, with a robust native fallback chain so the
    // app still looks intentional before the web font loads (or if it is blocked).
    // MudBlazor wraps any family name containing a space in quotes when it builds
    // the CSS, so these are left unquoted here.
    //
    // NOTE: this field is declared *before* Instance on purpose. Static field
    // initializers run in textual order, and Build() reads SansFont — declaring
    // it after Instance would hand Build() a null array and blank the fonts.
    private static readonly string[] SansFont =
    {
        "Inter",
        "-apple-system",
        "BlinkMacSystemFont",
        "Segoe UI",
        "Roboto",
        "Helvetica Neue",
        "Arial",
        "sans-serif",
    };

    /// <summary>The shared theme instance. Cheap to reference; build once.</summary>
    public static readonly MudTheme Instance = Build();

    private static MudTheme Build() => new()
    {
        PaletteLight = BuildLightPalette(),
        PaletteDark = BuildDarkPalette(),
        Typography = BuildTypography(),
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px",
            AppbarHeight = "64px",
            DrawerWidthLeft = "264px",
            DrawerMiniWidthLeft = "72px",
        },
    };

    // ----------------------------------------------------------------- Light
    private static PaletteLight BuildLightPalette() => new()
    {
        // Brand accent — indigo. Deep enough to read as professional on white.
        Primary = "#4F46E5",
        PrimaryContrastText = "#FFFFFF",
        PrimaryLighten = "#6366F1",
        PrimaryDarken = "#4338CA",

        Secondary = "#0E9AB8",
        SecondaryContrastText = "#FFFFFF",
        SecondaryLighten = "#22B8D6",
        SecondaryDarken = "#0B7E97",

        Tertiary = "#7C3AED",
        TertiaryContrastText = "#FFFFFF",
        TertiaryLighten = "#9457F0",
        TertiaryDarken = "#6A2BD6",

        Info = "#0284C7",
        InfoContrastText = "#FFFFFF",
        Success = "#0F9D6E",
        SuccessContrastText = "#FFFFFF",
        Warning = "#D97706",
        WarningContrastText = "#FFFFFF",
        Error = "#DC2626",
        ErrorContrastText = "#FFFFFF",
        ErrorLighten = "#EF4444",
        ErrorDarken = "#B91C1C",

        // Neutral surfaces — a cool, near-white canvas with crisp white cards.
        Background = "#F6F7FB",
        BackgroundGray = "#EDEFF5",
        Surface = "#FFFFFF",

        AppbarBackground = "#FFFFFF",
        AppbarText = "#161A23",
        DrawerBackground = "#FFFFFF",
        DrawerText = "#3B4254",
        DrawerIcon = "#5B6376",

        // Text & lines — slate ink at graded opacities for a calm hierarchy.
        TextPrimary = "rgba(17,21,29,0.92)",
        TextSecondary = "rgba(17,21,29,0.58)",
        TextDisabled = "rgba(17,21,29,0.36)",
        ActionDefault = "rgba(17,21,29,0.54)",
        ActionDisabled = "rgba(17,21,29,0.26)",
        ActionDisabledBackground = "rgba(17,21,29,0.10)",

        Divider = "rgba(17,21,29,0.08)",
        DividerLight = "rgba(17,21,29,0.05)",
        LinesDefault = "rgba(17,21,29,0.10)",
        LinesInputs = "rgba(17,21,29,0.22)",
        TableLines = "rgba(17,21,29,0.08)",
        TableStriped = "rgba(17,21,29,0.02)",
        TableHover = "rgba(17,21,29,0.04)",

        OverlayDark = "rgba(17,21,29,0.45)",
    };

    // ------------------------------------------------------------------ Dark
    private static PaletteDark BuildDarkPalette() => new()
    {
        // Lighter indigo so the accent stays vivid on near-black surfaces.
        // Dark contrast text keeps filled buttons crisp (a modern, Linear-ish look).
        Primary = "#7C8AFF",
        PrimaryContrastText = "#0A0C12",
        PrimaryLighten = "#9AA4FF",
        PrimaryDarken = "#6473F2",

        Secondary = "#22D3EE",
        SecondaryContrastText = "#06141A",
        SecondaryLighten = "#4DDEF3",
        SecondaryDarken = "#11B6D2",

        Tertiary = "#C084FC",
        TertiaryContrastText = "#0A0C12",
        TertiaryLighten = "#D2A6FD",
        TertiaryDarken = "#A961F0",

        Info = "#38BDF8",
        InfoContrastText = "#04121C",
        Success = "#34D399",
        SuccessContrastText = "#04140D",
        Warning = "#FBBF24",
        WarningContrastText = "#1A1303",
        Error = "#FB7185",
        ErrorContrastText = "#1A0509",
        ErrorLighten = "#FF93A6",
        ErrorDarken = "#F1556C",

        // Deep "ink" canvas with a subtly elevated surface for cards/menus.
        Black = "#06080D",
        Background = "#0B0E15",
        BackgroundGray = "#171C28",
        Surface = "#12161F",

        AppbarBackground = "#0E121B",
        AppbarText = "#E6E9F2",
        DrawerBackground = "#0E121B",
        DrawerText = "#AEB6C8",
        DrawerIcon = "#838CA0",

        // Text & lines — soft off-white at graded opacities.
        TextPrimary = "rgba(236,239,246,0.92)",
        TextSecondary = "rgba(236,239,246,0.56)",
        TextDisabled = "rgba(236,239,246,0.32)",
        ActionDefault = "rgba(236,239,246,0.60)",
        ActionDisabled = "rgba(236,239,246,0.26)",
        ActionDisabledBackground = "rgba(236,239,246,0.10)",

        Divider = "rgba(236,239,246,0.09)",
        DividerLight = "rgba(236,239,246,0.05)",
        LinesDefault = "rgba(236,239,246,0.11)",
        LinesInputs = "rgba(236,239,246,0.22)",
        TableLines = "rgba(236,239,246,0.09)",
        TableStriped = "rgba(236,239,246,0.03)",
        TableHover = "rgba(236,239,246,0.05)",

        OverlayDark = "rgba(6,8,13,0.72)",
        OverlayLight = "rgba(236,239,246,0.10)",
    };

    // ------------------------------------------------------------ Typography
    private static Typography BuildTypography() => new()
    {
        Default = new DefaultTypography
        {
            FontFamily = SansFont,
            FontSize = "0.9375rem",
            FontWeight = "400",
            LineHeight = "1.55",
            LetterSpacing = "normal",
        },
        H1 = new H1Typography { FontFamily = SansFont, FontSize = "2.5rem", FontWeight = "700", LineHeight = "1.18", LetterSpacing = "-0.022em" },
        H2 = new H2Typography { FontFamily = SansFont, FontSize = "2rem", FontWeight = "700", LineHeight = "1.2", LetterSpacing = "-0.021em" },
        H3 = new H3Typography { FontFamily = SansFont, FontSize = "1.625rem", FontWeight = "700", LineHeight = "1.22", LetterSpacing = "-0.02em" },
        H4 = new H4Typography { FontFamily = SansFont, FontSize = "1.375rem", FontWeight = "600", LineHeight = "1.28", LetterSpacing = "-0.018em" },
        H5 = new H5Typography { FontFamily = SansFont, FontSize = "1.15rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-0.014em" },
        H6 = new H6Typography { FontFamily = SansFont, FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "-0.01em" },
        Subtitle1 = new Subtitle1Typography { FontFamily = SansFont, FontSize = "1rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "-0.006em" },
        Subtitle2 = new Subtitle2Typography { FontFamily = SansFont, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0" },
        Body1 = new Body1Typography { FontFamily = SansFont, FontSize = "0.9375rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "normal" },
        Body2 = new Body2Typography { FontFamily = SansFont, FontSize = "0.8125rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "normal" },
        // The signature modern touch: real-case, medium-weight buttons (no ALL-CAPS).
        Button = new ButtonTypography { FontFamily = SansFont, FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.75", LetterSpacing = "0.01em", TextTransform = "none" },
        Caption = new CaptionTypography { FontFamily = SansFont, FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.45", LetterSpacing = "0.01em" },
        Overline = new OverlineTypography { FontFamily = SansFont, FontSize = "0.6875rem", FontWeight = "600", LineHeight = "1.6", LetterSpacing = "0.08em", TextTransform = "uppercase" },
    };
}
