## Theming
- Avalonia theme system with custom trading palettes
- Built-in: Dark (default), Light, Midnight Blue, OLED Black
- Custom themes via JSON files in themes/ directory
- Users can create/edit themes without recompiling
- Theme JSON format includes: name, displayName, baseTheme, colors, previewColors
- Color keys: Background, Surface, Border, Accent, TitleBar, TitleBarText, Highlight, TextPrimary, TextSecondary, Buy, Sell, CandleUp, CandleDown, ChartBackground, ChartGrid, Error
- Chart-specific colors: CandleUp, CandleDown, Buy, Sell, Grid
- Runtime theme switching via ThemeManager service
- Sample theme created automatically on first run in themes/ directory
