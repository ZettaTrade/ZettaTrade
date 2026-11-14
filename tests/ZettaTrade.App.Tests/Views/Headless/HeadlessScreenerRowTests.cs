namespace ZettaTrade.App.Tests.Views.Headless;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using Xunit;
using ZettaTrade.App.ViewModels;
using ZettaTrade.App.Views;

/// <summary>
/// The headless screener-row facts (T-232): the REAL screener window
/// materializes without a view model, the rows ride in through a direct
/// ItemsSource, and the row press target is MEASURED — the T-232 probe
/// named the failure (Avalonia 12's fluent ControlTheme carries
/// HorizontalAlignment=Left on every button, so the T-195
/// content-alignment setter stretched only the row's content and every
/// row sat at its own width under the shared header plan), and the facts
/// pin the fix on the real layout: the rows stretch to the header plan
/// and trade the fluent button chrome for the row grammar's hairline
/// (square corners, the bottom separator), and the header's sort buttons
/// ride the same stretch so the captions align over the data columns.
/// The window materializes WITHOUT a view model: the bindings fail
/// silently (the T-231 dialog precedent), and every window touch rides
/// the world's user-interface lane. The facts share the
/// LocalizationManager singleton, so the class rides the
/// LocalizationManagerSingleton collection.
/// </summary>
[Collection("LocalizationManagerSingleton")]
public sealed class HeadlessScreenerRowTests
{
    static HeadlessScreenerRowTests() => AvaloniaDispatcherPump.EnsureInstalled();

    /// <summary>
    /// The row press target stretches to the header plan and carries the
    /// row grammar's chrome: the button's width equals the header row's
    /// (the shared column plan, not the row's own content width), the
    /// corners ride square and the separator is the bottom hairline —
    /// the fluent button chrome (the 1px rounded border around every
    /// row) is gone.
    /// </summary>
    [Fact]
    public async Task TheRowPressTarget_StretchToTheHeaderPlan_WithTheRowHairline()
    {
        var (window, header, rows) = await MaterializeAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                rows.Should().HaveCount(2, "the materialized rows stand the measured facts");
                foreach (var (button, index) in rows.Select((b, i) => (b, i)))
                {
                    button.Bounds.Width.Should().BeApproximately(
                        header.Bounds.Width, 0.5,
                        "the row {0} stretches to the header plan (the Avalonia 12 Left " +
                        "default never steers the whole-row press target)", index);
                    button.CornerRadius.TopLeft.Should().Be(0,
                        "the row trades the fluent chrome's rounded corners for the table look");
                    button.BorderThickness.Should().Be(
                        new Thickness(0, 0, 0, 1),
                        "the row's only chrome is the row grammar's bottom hairline");
                }
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// The header's sort buttons ride the same stretch: each fills its
    /// grid column, so the right-aligned captions land on the column's
    /// right edge — the edge the data cells' numbers align to. The price
    /// column is the fact's witness (the `*` plan): the sort button's
    /// width equals the data cell's width, so the header caption and the
    /// cell numbers share one right edge.
    /// </summary>
    [Fact]
    public async Task TheSortHeaders_RideTheStretch_AndAlignOverTheDataColumns()
    {
        var (window, _, rows) = await MaterializeAsync();
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var header = HeadlessWorld.Descendants<Border>(window)
                    .Single(b => b.Classes.Contains("table-header-row"));
                var sorts = header.GetVisualChildren().OfType<Visual>()
                    .SelectMany(HeadlessWorld.Descendants<Button>)
                    .Where(b => b.Classes.Contains("column-sort")).ToList();
                sorts.Should().HaveCount(3, "the price, change and volume columns sort");
                foreach (var sort in sorts)
                {
                    sort.HorizontalAlignment.Should().Be(HorizontalAlignment.Stretch,
                        "the sort button fills its column (the Avalonia 12 Left default " +
                        "never lands the right-aligned caption on the column edge)");
                }

                // The row's data grid sits one template presenter deep: the
                // button's template host, then the column-plan grid.
                var rowGrid = HeadlessWorld.Descendants<Grid>(rows[0]).First();
                var priceCell = rowGrid.Children.OfType<TextBlock>().ToList()[1];
                sorts[0].Bounds.Width.Should().BeApproximately(
                    priceCell.Bounds.Width, 0.5,
                    "the price sort caption and the data cells ride the same `*` plan");
            });
        }
        finally
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
        }
    }

    /// <summary>
    /// Materialize the screener window headlessly, inject two rows through
    /// the row list's direct ItemsSource (the preset picker's ComboBox is
    /// an ItemsControl too — the row list is the non-ComboBox one), force
    /// the table visible over the null view model's bindings, and settle
    /// the layout. Returns the window, the header border and the row
    /// buttons in document order.
    /// </summary>
    private static async Task<(Window Window, Border Header, IReadOnlyList<Button> Rows)> MaterializeAsync()
    {
        await HeadlessWorld.EnsureAppAsync();
        await HeadlessWorld.EnsureHealthyThemeSingletonAsync();

        var window = await HeadlessWorld.OnUi(() => new ScreenerWindow());
        await HeadlessWorld.ShowAndSettleAsync(window);
        try
        {
            await HeadlessWorld.OnUi(() =>
            {
                var items = HeadlessWorld.Descendants<ItemsControl>(window)
                    .Single(control => control is not ComboBox);
                var header = HeadlessWorld.Descendants<Border>(window)
                    .Single(b => b.Classes.Contains("table-header-row"));
                header.IsVisible = true;
                items.IsVisible = true;
                items.ItemsSource = new List<ScreenerRowViewModel>
                {
                    new()
                    {
                        Symbol = "BTC/USDT", Price = "97 412,50", Change = "+2,4%",
                        Volume = "1,2 млрд", Funding = "0,0100%",
                        IsChangeUp = true, IsChangeDown = false,
                    },
                    new()
                    {
                        Symbol = "ETH/USDT", Price = "3 612,20", Change = "-1,1%",
                        Volume = "840 млн", Funding = "0,0095%",
                        IsChangeUp = false, IsChangeDown = true,
                    },
                };
            });

            // settle the materialization + layout
            await HeadlessWorld.OnUi(() => { });

            return await HeadlessWorld.OnUi<(Window, Border, IReadOnlyList<Button>)>(() =>
            {
                var header = HeadlessWorld.Descendants<Border>(window)
                    .Single(b => b.Classes.Contains("table-header-row"));
                var rows = HeadlessWorld.Descendants<Button>(window)
                    .Where(b => b.Classes.Contains("screener-row"))
                    .OrderBy(b => b.Bounds.Position.Y)
                    .ToList();
                return (window, header, rows);
            });
        }
        catch
        {
            await HeadlessWorld.CloseAndSettleAsync(window);
            throw;
        }
    }
}
