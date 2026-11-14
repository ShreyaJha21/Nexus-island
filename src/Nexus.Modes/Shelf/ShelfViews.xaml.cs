using System.Windows;
using System.Windows.Controls;
using Nexus.Modes.Controls;

namespace Nexus.Modes.Shelf;

public partial class ShelfCompactView : UserControl
{
    public ShelfCompactView()
    {
        InitializeComponent();

        // Dragging the pill takes everything on the shelf.
        _ = new DragStarter(this, () =>
        {
            if (DataContext is ShelfMode shelf)
            {
                shelf.DragOut(this, shelf.Items.ToList());
            }
        });
    }
}

public partial class ShelfPanelView : UserControl
{
    public ShelfPanelView() => InitializeComponent();

    /// <summary>Each tile drags its own item.</summary>
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement tile && tile.Tag is null)
        {
            tile.Tag = new DragStarter(tile, () =>
            {
                if (tile.DataContext is ShelfItemView item)
                {
                    item.Shelf.DragOut(tile, [item]);
                }
            });
        }
    }
}

public partial class ShelfBubbleView : UserControl
{
    public ShelfBubbleView()
    {
        InitializeComponent();
        _ = new DragStarter(this, () =>
        {
            if (DataContext is ShelfMode { Items.Count: > 0 } shelf)
            {
                shelf.DragOut(this, [shelf.Items[0]]);
            }
        });
    }
}
