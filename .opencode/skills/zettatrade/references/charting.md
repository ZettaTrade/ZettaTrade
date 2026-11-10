## Chart Rendering (Phase 2)
- **Control**: CandlestickChart using SkiaSharp
- **Features**:
  - Real-time updates via ObservableCollection of Bar
  - Zoom and pan with mouse wheel and drag
  - Price and time axes with auto-scaling
  - Bullish/bearish candle coloring
  - Grid lines and crosshair (planned)
- **ViewModel**: ChartViewModel with symbol selection and data loading
- **Performance**: Hardware-accelerated rendering via SkiaSharp
