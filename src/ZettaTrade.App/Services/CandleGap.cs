namespace ZettaTrade.App.Services;

using System;

/// <summary>
/// One run of calendar-expected opens with no bar in the series (T-086):
/// <see cref="FirstMissingOpen"/> and <see cref="LastMissingOpen"/> are both
/// genuinely missing bar opens (the repair fetch window is
/// <c>[FirstMissingOpen, NextOpenAfter(LastMissingOpen))</c> — the exclusive
/// upper edge walks off the same calendar), and <see cref="MissingBars"/> is
/// the enumerated width. <see cref="IsTruncated"/> marks a hole whose run
/// reached the detector's bound and continues wider: a bounded repair pass
/// skips it rather than chewing an open-ended hole per delivery (the
/// batching discipline against the T-106 limiter).
/// </summary>
public readonly record struct CandleGap(
    DateTimeOffset FirstMissingOpen,
    DateTimeOffset LastMissingOpen,
    int MissingBars,
    bool IsTruncated);
